using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.PowerQuery;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.Lineage.PowerBi;

/// <summary>What translating one visual's query produced: T-SQL over the source tables, or why there is none.</summary>
internal sealed record VisualTranslation(string? Sql, string? Problem)
{
    public static VisualTranslation Failed(string problem) => new(null, problem);
}

/// <summary>
/// Translates the query a Power BI visual asks, which names the report's MODEL (tables, possibly renamed or computed
/// columns, measures, with no joins and no grouping), into one T-SQL statement over the source tables the model
/// loads from. The report is the authority on how its tables relate and what its numbers mean, so the statement is
/// built from the model itself:
/// <list type="bullet">
/// <item>each model table becomes the source relation its Power Query produces (<see cref="PowerQueryLineage"/>);</item>
/// <item>the tables are joined along the model's relationships, from the table the others filter;</item>
/// <item>every column the visual groups by becomes a <c>GROUP BY</c> key, and every measure its T-SQL aggregate
/// (<see cref="DaxTranslator"/>);</item>
/// <item>a measure that activates a different relationship is computed over its own join and joined back on the
/// group keys.</item>
/// </list>
/// The output is T-SQL only. Anything that cannot be expressed exactly is refused with the reason rather than
/// approximated, because an approximate query answers a different question under the same title.
/// </summary>
internal sealed class VisualSqlTranslator
{
    private readonly IReadOnlyDictionary<string, LineageSubscriberModelTable> _tables;
    private readonly IReadOnlyList<LineageSubscriberModelRelationship> _relationships;
    private readonly IReadOnlyDictionary<string, string> _queries;
    private readonly Func<SourceTable, IReadOnlyCollection<string>?> _knownColumns;
    private readonly DaxTranslator _dax;
    private readonly Dictionary<string, Relation> _relations = new(StringComparer.Ordinal);

    /// <param name="model">The report's semantic model.</param>
    /// <param name="knownColumns">The columns a source table is known to have, or null when they are not known.</param>
    public VisualSqlTranslator(LineageSubscriberModel model, Func<SourceTable, IReadOnlyCollection<string>?> knownColumns)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(knownColumns);
        _knownColumns = knownColumns;

        var tables = new Dictionary<string, LineageSubscriberModelTable>(StringComparer.Ordinal);
        foreach (var table in model.Tables)
        {
            tables.TryAdd(table.Name, table);
        }

        _tables = tables;
        _relationships = model.Relationships;

        // A table's query can merge in any other query by name: the shared expressions and the tables' own queries.
        var queries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expression in model.Expressions)
        {
            queries.TryAdd(expression.Name, expression.PowerQuery);
        }

        foreach (var table in model.Tables.Where(t => !string.IsNullOrWhiteSpace(t.PowerQuery)))
        {
            queries.TryAdd(table.Name, table.PowerQuery!);
        }

        _queries = queries;

        var measures = new Dictionary<(string Table, string Name), string>();
        foreach (var table in model.Tables)
        {
            foreach (var field in table.Fields.Where(f => f.Kind == "measure" && !string.IsNullOrWhiteSpace(f.Expression)))
            {
                measures.TryAdd((table.Name, field.Name), field.Expression!);
            }
        }

        _dax = new DaxTranslator(measures);
    }

    public VisualTranslation Translate(string visualSql)
    {
        ArgumentNullException.ThrowIfNull(visualSql);
        try
        {
            var sql = Build(visualSql);
            ReadOnlyQueryGuard.Validate(sql);
            return new VisualTranslation(sql, null);
        }
        catch (TranslationException ex)
        {
            return VisualTranslation.Failed(ex.Message);
        }
        catch (Core.SqlFlowException ex)
        {
            return VisualTranslation.Failed($"the translated query was refused ({ex.Message})");
        }
    }

    // ---- The visual's query ---------------------------------------------------------------------------------

    private abstract record Item(string Alias);

    private sealed record KeyItem(string Alias, ModelColumn Column) : Item(Alias);

    private sealed record AggregateItem(string Alias, AggExpr Expression, string Context) : Item(Alias);

    private string Build(string visualSql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(visualSql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            throw new TranslationException($"the report's query does not parse ({errors[0].Message})");
        }

        var statements = (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).ToList() ?? [];
        if (statements.Count != 1
            || statements[0] is not SelectStatement { QueryExpression: QuerySpecification query } select
            || select.WithCtesAndXmlNamespaces is not null
            || query.FromClause is null)
        {
            throw new TranslationException("the report's query is not a single SELECT over model tables");
        }

        var entities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in query.FromClause.TableReferences)
        {
            if (reference is not NamedTableReference { SchemaObject: { Identifiers.Count: 1 } name } named)
            {
                throw new TranslationException("the report's query reads something other than model tables");
            }

            var entity = name.BaseIdentifier.Value;
            if (!_tables.ContainsKey(entity))
            {
                throw new TranslationException($"the report's query reads '{entity}', which is not a table of the model");
            }

            entities[named.Alias?.Value ?? entity] = entity;
        }

        var items = new List<Item>();
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in query.SelectElements)
        {
            if (element is not SelectScalarExpression { ColumnName: { } columnName } scalar)
            {
                throw new TranslationException("the report's query selects something without a name");
            }

            var alias = columnName.Value;
            items.Add(Classify(scalar.Expression, alias, entities));
            originals[Script(scalar.Expression)] = alias;
        }

        var filters = query.WhereClause?.SearchCondition;
        var filterColumns = new List<ModelColumn>();
        if (filters is not null)
        {
            CollectColumns(filters, entities, filterColumns);
        }

        var orderBy = new List<string>();
        foreach (var order in query.OrderByClause?.OrderByElements ?? [])
        {
            if (!originals.TryGetValue(Script(order.Expression), out var alias))
            {
                throw new TranslationException("the report's query orders by something it does not select");
            }

            orderBy.Add(SqlRenderer.Quote(alias) + (order.SortOrder == SortOrder.Descending ? " DESC" : " ASC"));
        }

        var keys = items.OfType<KeyItem>().ToList();
        var aggregates = items.OfType<AggregateItem>().ToList();
        var contexts = aggregates.Select(a => a.Context).Distinct(StringComparer.Ordinal).ToList();
        if (contexts.Count == 0)
        {
            contexts.Add(string.Empty);
        }

        var tail = orderBy.Count > 0 ? "\nORDER BY " + string.Join(", ", orderBy) : string.Empty;
        if (contexts.Count == 1)
        {
            var single = Context(contexts[0], keys, aggregates, filters, filterColumns, entities, keyAliases: null);
            return single + tail;
        }

        return Combined(contexts, keys, aggregates, filters, filterColumns, entities, items) + tail;
    }

    private Item Classify(ScalarExpression expression, string alias, IReadOnlyDictionary<string, string> entities)
    {
        switch (expression)
        {
            case ColumnReferenceExpression reference:
            {
                var column = ModelColumnOf(reference, entities);
                if (_dax.IsMeasure(column.Table, column.Column))
                {
                    var measure = _dax.Measure(column.Table, column.Column);
                    return new AggregateItem(alias, measure, ContextOf(measure));
                }

                return new KeyItem(alias, column);
            }

            case FunctionCall { Parameters: [ColumnReferenceExpression argument] } call:
            {
                var function = call.FunctionName.Value.ToUpperInvariant();
                if (function is not ("SUM" or "AVG" or "COUNT" or "MIN" or "MAX"))
                {
                    throw new TranslationException($"the report's query aggregates with {function}");
                }

                var column = ModelColumnOf(argument, entities);
                if (_dax.IsMeasure(column.Table, column.Column))
                {
                    throw new TranslationException($"the report's query aggregates the measure [{column.Column}]");
                }

                return new AggregateItem(
                    alias,
                    new AggFunction(function, false, column, null, new HashSet<RelationshipOverride>()),
                    string.Empty);
            }

            default:
                throw new TranslationException($"the report's query selects '{Script(expression)}', which is not translated");
        }
    }

    private static ModelColumn ModelColumnOf(ColumnReferenceExpression reference, IReadOnlyDictionary<string, string> entities)
    {
        if (reference.MultiPartIdentifier is not { Identifiers.Count: 2 } parts)
        {
            throw new TranslationException("the report's query names a column without its table");
        }

        var alias = parts.Identifiers[0].Value;
        if (!entities.TryGetValue(alias, out var entity))
        {
            throw new TranslationException($"the report's query names '{alias}', which its FROM does not bind");
        }

        return new ModelColumn(entity, parts.Identifiers[1].Value);
    }

    // A join context is identified by the relationships its aggregates activate; the default context activates none.
    private static string ContextOf(AggExpr expression)
    {
        var sets = Functions(expression)
            .Select(f => string.Join(";", f.Overrides
                .Select(o => $"{o.TableA}[{o.ColumnA}]={o.TableB}[{o.ColumnB}]")
                .Order(StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return sets.Count switch
        {
            0 => string.Empty,
            1 => sets[0],
            _ => throw new TranslationException("a measure combines values computed over different relationships"),
        };
    }

    private static IEnumerable<AggFunction> Functions(AggExpr expression) => expression switch
    {
        AggFunction function => [function],
        AggBinary binary => Functions(binary.Left).Concat(Functions(binary.Right)),
        AggNegate negate => Functions(negate.Operand),
        AggDivide divide => Functions(divide.Numerator).Concat(Functions(divide.Denominator))
            .Concat(divide.Alternate is null ? [] : Functions(divide.Alternate)),
        _ => [],
    };

    private void CollectColumns(TSqlFragment fragment, IReadOnlyDictionary<string, string> entities, List<ModelColumn> columns)
    {
        var visitor = new ColumnCollector();
        fragment.Accept(visitor);
        foreach (var reference in visitor.Columns)
        {
            var column = ModelColumnOf(reference, entities);
            if (_dax.IsMeasure(column.Table, column.Column))
            {
                throw new TranslationException($"the report's query filters on the measure [{column.Column}]");
            }

            columns.Add(column);
        }
    }

    private sealed class ColumnCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> Columns { get; } = [];

        public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);
    }

    private static string Script(TSqlFragment fragment)
    {
        var builder = new StringBuilder();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            builder.Append(fragment.ScriptTokenStream[i].Text);
        }

        return builder.ToString();
    }

    // ---- One join context -----------------------------------------------------------------------------------

    private string Context(
        string context, IReadOnlyList<KeyItem> keys, IReadOnlyList<AggregateItem> aggregates,
        BooleanExpression? filters, IReadOnlyList<ModelColumn> filterColumns, IReadOnlyDictionary<string, string> entities,
        IReadOnlyList<string>? keyAliases)
    {
        var mine = aggregates.Where(a => string.Equals(a.Context, context, StringComparison.Ordinal)).ToList();
        var overrides = mine.SelectMany(a => Functions(a.Expression)).SelectMany(f => f.Overrides).Distinct().ToList();

        var needed = new List<string>();
        void Need(string table)
        {
            if (!needed.Contains(table, StringComparer.Ordinal))
            {
                needed.Add(table);
            }
        }

        // The aggregates' tables first: the table a measure counts is where the join starts when it can.
        foreach (var function in mine.SelectMany(a => Functions(a.Expression)))
        {
            Need(function.Column?.Table ?? function.RowsOf!);
        }

        foreach (var key in keys)
        {
            Need(key.Column.Table);
        }

        foreach (var column in filterColumns)
        {
            Need(column.Table);
        }

        foreach (var o in overrides)
        {
            Need(o.TableA);
            Need(o.TableB);
        }

        var plan = PlanJoins(needed, overrides);
        var renderer = new SqlRenderer();
        var used = new List<SqlScalar>();

        SqlScalar ScalarOf(ModelColumn column)
        {
            var value = RelationOf(column.Table).Column(column.Column);
            if (value.Expression is null)
            {
                throw new TranslationException($"'{column.Table}'[{column.Column}] cannot be expressed: {value.Problem}");
            }

            used.Add(value.Expression);
            return value.Expression;
        }

        string Aggregate(AggExpr expression) => expression switch
        {
            AggFunction { Column: null } count => string.Equals(count.RowsOf, plan.Root, StringComparison.Ordinal)
                ? "COUNT_BIG(*)"
                : throw new TranslationException($"COUNTROWS('{count.RowsOf}') counts a table the visual's rows are not built from"),
            AggFunction { Function: "AVG" } average => $"AVG(CAST({renderer.Scalar(ScalarOf(average.Column!))} AS float))",
            AggFunction function => $"{function.Function}({(function.Distinct ? "DISTINCT " : string.Empty)}{renderer.Scalar(ScalarOf(function.Column!))})",
            AggNumber number => number.Value,
            AggNull => "NULL",
            AggNegate negate => $"(-{Aggregate(negate.Operand)})",
            AggBinary { Operator: "/" } division => $"(CAST({Aggregate(division.Left)} AS float) / NULLIF({Aggregate(division.Right)}, 0))",
            AggBinary binary => $"({Aggregate(binary.Left)} {binary.Operator} {Aggregate(binary.Right)})",
            AggDivide divide => divide.Alternate is null
                ? $"(CAST({Aggregate(divide.Numerator)} AS float) / NULLIF({Aggregate(divide.Denominator)}, 0))"
                : $"COALESCE(CAST({Aggregate(divide.Numerator)} AS float) / NULLIF({Aggregate(divide.Denominator)}, 0), {Aggregate(divide.Alternate)})",
            _ => throw new TranslationException("an aggregate is not translated"),
        };

        var select = new List<string>();
        var groupBy = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var expression = renderer.Scalar(ScalarOf(keys[i].Column));
            select.Add($"{expression} AS {SqlRenderer.Quote(keyAliases?[i] ?? keys[i].Alias)}");
            groupBy.Add(expression);
        }

        for (var i = 0; i < mine.Count; i++)
        {
            var alias = keyAliases is null ? mine[i].Alias : "a" + i.ToString(CultureInfo.InvariantCulture);
            select.Add($"{Aggregate(mine[i].Expression)} AS {SqlRenderer.Quote(alias)}");
        }

        var from = new StringBuilder(renderer.Relation(RelationOf(plan.Root)));
        foreach (var step in plan.Steps)
        {
            var condition = step.Pairs
                .Select(p => $"{renderer.Scalar(ScalarOf(p.Parent))} = {renderer.Scalar(ScalarOf(p.Child))}");
            from.Append("\nLEFT JOIN ").Append(renderer.Relation(RelationOf(step.Table)))
                .Append(" ON ").Append(string.Join(" AND ", condition));
        }

        var where = filters is null ? null : Predicate(filters, entities, renderer, ScalarOf);

        foreach (var relation in plan.Tables().Select(RelationOf))
        {
            used.AddRange(relation.Joins.SelectMany(JoinScalars));
        }

        Verify(used);

        var sql = new StringBuilder("SELECT ").Append(string.Join(", ", select))
            .Append("\nFROM ").Append(from);
        if (where is not null)
        {
            sql.Append("\nWHERE ").Append(where);
        }

        if (groupBy.Count > 0)
        {
            sql.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));
        }

        return sql.ToString();
    }

    private static IEnumerable<SqlScalar> JoinScalars(RelationJoin join)
        => join.On.SelectMany(p => new[] { p.Left, p.Right }).Concat(join.Right.Joins.SelectMany(JoinScalars));

    private void Verify(IEnumerable<SqlScalar> scalars)
    {
        foreach (var column in scalars.SelectMany(Columns))
        {
            if (_knownColumns(column.Table) is { } known
                && !known.Contains(column.Column, StringComparer.OrdinalIgnoreCase))
            {
                throw new TranslationException(
                    $"the source table {column.Table.Label} has no column '{column.Column}'");
            }
        }
    }

    private static IEnumerable<SqlColumn> Columns(SqlScalar scalar) => scalar switch
    {
        SqlColumn column => [column],
        SqlConcat concat => concat.Parts.SelectMany(Columns),
        SqlArithmetic arithmetic => Columns(arithmetic.Left).Concat(Columns(arithmetic.Right)),
        SqlNegate negate => Columns(negate.Operand),
        SqlCast cast => Columns(cast.Operand),
        _ => [],
    };

    private Relation RelationOf(string table)
    {
        if (_relations.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var model = _tables[table];
        if (string.IsNullOrWhiteSpace(model.PowerQuery))
        {
            throw new TranslationException($"model table '{table}' has no Power Query to read its source from");
        }

        var relation = PowerQueryLineage.Resolve(model.PowerQuery, _queries);
        if (relation.Problem is not null)
        {
            throw new TranslationException($"model table '{table}' cannot be expressed: {relation.Problem}");
        }

        _relations[table] = relation;
        return relation;
    }

    // ---- Filters --------------------------------------------------------------------------------------------

    private static string Predicate(
        BooleanExpression expression, IReadOnlyDictionary<string, string> entities, SqlRenderer renderer,
        Func<ModelColumn, SqlScalar> scalarOf)
    {
        string Value(ScalarExpression value) => value switch
        {
            ColumnReferenceExpression reference => renderer.Scalar(scalarOf(ModelColumnOf(reference, entities))),
            IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral => ((Literal)value).Value,
            StringLiteral text => SqlRenderer.TextLiteral(text.Value),
            NullLiteral => "NULL",
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } negative => "-" + Value(negative.Expression),
            ParenthesisExpression parenthesis => "(" + Value(parenthesis.Expression) + ")",
            _ => throw new TranslationException($"the report's filter uses '{Script(value)}', which is not translated"),
        };

        switch (expression)
        {
            case BooleanParenthesisExpression parenthesis:
                return "(" + Predicate(parenthesis.Expression, entities, renderer, scalarOf) + ")";
            case BooleanNotExpression not:
                return "NOT (" + Predicate(not.Expression, entities, renderer, scalarOf) + ")";
            case BooleanBinaryExpression binary:
                return "(" + Predicate(binary.FirstExpression, entities, renderer, scalarOf)
                    + (binary.BinaryExpressionType == BooleanBinaryExpressionType.And ? " AND " : " OR ")
                    + Predicate(binary.SecondExpression, entities, renderer, scalarOf) + ")";
            case BooleanIsNullExpression isNull:
                return Value(isNull.Expression) + (isNull.IsNot ? " IS NOT NULL" : " IS NULL");
            case BooleanComparisonExpression comparison:
            {
                var op = comparison.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "=",
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "<>",
                    BooleanComparisonType.GreaterThan => ">",
                    BooleanComparisonType.GreaterThanOrEqualTo => ">=",
                    BooleanComparisonType.LessThan => "<",
                    BooleanComparisonType.LessThanOrEqualTo => "<=",
                    _ => throw new TranslationException("the report's filter uses an unsupported comparison"),
                };

                // A blank in Power BI is NULL here, and "= blank" means IS NULL.
                if (comparison.SecondExpression is NullLiteral && op is "=" or "<>")
                {
                    return Value(comparison.FirstExpression) + (op == "=" ? " IS NULL" : " IS NOT NULL");
                }

                return $"({Value(comparison.FirstExpression)} {op} {Value(comparison.SecondExpression)})";
            }

            case InPredicate { Subquery: null } inList:
            {
                var subject = Value(inList.Expression);
                var values = inList.Values.Where(v => v is not NullLiteral).Select(Value).ToList();
                var hasBlank = inList.Values.Any(v => v is NullLiteral);
                var membership = values.Count == 0 ? null : $"{subject} IN ({string.Join(", ", values)})";
                var test = (membership, hasBlank) switch
                {
                    (null, true) => $"{subject} IS NULL",
                    (_, true) => $"({membership} OR {subject} IS NULL)",
                    _ => membership ?? "1 = 0",
                };
                return inList.NotDefined ? $"NOT ({test})" : test;
            }

            default:
                throw new TranslationException($"the report's filter uses '{Script(expression)}', which is not translated");
        }
    }

    // ---- Joins ----------------------------------------------------------------------------------------------

    private sealed record JoinPair(ModelColumn Parent, ModelColumn Child);

    private sealed record JoinStep(string Table, IReadOnlyList<JoinPair> Pairs);

    private sealed record JoinPlan(string Root, IReadOnlyList<JoinStep> Steps)
    {
        public IEnumerable<string> Tables() => Steps.Select(s => s.Table).Prepend(Root);
    }

    private sealed record Edge(string From, string To, JoinPair Pair);

    /// <summary>
    /// Joins the needed tables from a root: the table that reaches every other needed table by following the model's
    /// relationships in the direction a filter flows (from the many side to the one side). The relationships a
    /// context activates replace the model's others between the same two tables.
    /// </summary>
    private JoinPlan PlanJoins(IReadOnlyList<string> needed, IReadOnlyList<RelationshipOverride> overrides)
    {
        var edges = new List<Edge>();
        foreach (var relationship in _relationships)
        {
            if (relationship.FromColumn is null || relationship.ToColumn is null)
            {
                continue;
            }

            var overridden = overrides.Any(o => o.Connects(relationship.FromTable, relationship.ToTable));
            var chosen = overrides.Any(o =>
                RelationshipOverride.Normalize(
                    new ModelColumn(relationship.FromTable, relationship.FromColumn),
                    new ModelColumn(relationship.ToTable, relationship.ToColumn)) == o);
            if (overridden ? !chosen : !relationship.IsActive)
            {
                continue;
            }

            var from = new ModelColumn(relationship.FromTable, relationship.FromColumn);
            var to = new ModelColumn(relationship.ToTable, relationship.ToColumn);
            switch (relationship.Cardinality)
            {
                case "M:1" or null:
                    edges.Add(new Edge(from.Table, to.Table, new JoinPair(from, to)));
                    break;
                case "1:M":
                    edges.Add(new Edge(to.Table, from.Table, new JoinPair(to, from)));
                    break;
                case "1:1":
                    edges.Add(new Edge(from.Table, to.Table, new JoinPair(from, to)));
                    edges.Add(new Edge(to.Table, from.Table, new JoinPair(to, from)));
                    break;
                default:
                    // A many-to-many relationship is not a join a visual's rows can be built along.
                    break;
            }
        }

        foreach (var o in overrides)
        {
            var present = _relationships.Any(r => r.FromColumn is not null && r.ToColumn is not null
                && RelationshipOverride.Normalize(new ModelColumn(r.FromTable, r.FromColumn), new ModelColumn(r.ToTable, r.ToColumn)) == o);
            if (!present)
            {
                throw new TranslationException(
                    $"USERELATIONSHIP names '{o.TableA}'[{o.ColumnA}] and '{o.TableB}'[{o.ColumnB}], which the model does not relate");
            }
        }

        var candidates = needed.Concat(_tables.Keys.Where(t => !needed.Contains(t, StringComparer.Ordinal)));
        foreach (var root in candidates)
        {
            var parents = new Dictionary<string, Edge>(StringComparer.Ordinal);
            var order = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<string>([root]);
            while (queue.Count > 0)
            {
                var table = queue.Dequeue();
                foreach (var edge in edges.Where(e => string.Equals(e.From, table, StringComparison.Ordinal)))
                {
                    if (visited.Add(edge.To))
                    {
                        parents[edge.To] = edge;
                        order.Add(edge.To);
                        queue.Enqueue(edge.To);
                    }
                }
            }

            if (!needed.All(visited.Contains))
            {
                continue;
            }

            // Keep only the tables on a path from the root to a needed table.
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in needed)
            {
                var current = table;
                while (!string.Equals(current, root, StringComparison.Ordinal) && keep.Add(current))
                {
                    current = parents[current].From;
                }
            }

            var steps = order.Where(keep.Contains)
                .Select(t => new JoinStep(t, [parents[t].Pair]))
                .ToList();
            return new JoinPlan(root, steps);
        }

        throw new TranslationException(
            $"no table of the model relates {string.Join(", ", needed.Select(t => $"'{t}'"))} through its active relationships");
    }

    // ---- Several join contexts ------------------------------------------------------------------------------

    private string Combined(
        IReadOnlyList<string> contexts, IReadOnlyList<KeyItem> keys, IReadOnlyList<AggregateItem> aggregates,
        BooleanExpression? filters, IReadOnlyList<ModelColumn> filterColumns, IReadOnlyDictionary<string, string> entities,
        IReadOnlyList<Item> items)
    {
        var keyAliases = keys.Select((_, i) => "k" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        var ctes = new List<string>();
        var names = new List<string>();
        for (var c = 0; c < contexts.Count; c++)
        {
            var name = "q" + c.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            ctes.Add($"{SqlRenderer.Quote(name)} AS (\n{Context(contexts[c], keys, aggregates, filters, filterColumns, entities, keyAliases)}\n)");
        }

        string KeyOf(string cte, int key) => $"{SqlRenderer.Quote(cte)}.{SqlRenderer.Quote(keyAliases[key])}";

        string MergedKey(int key, int upTo) => upTo == 0
            ? KeyOf(names[0], key)
            : "COALESCE(" + string.Join(", ", names.Take(upTo + 1).Select(n => KeyOf(n, key))) + ")";

        var from = new StringBuilder(SqlRenderer.Quote(names[0]));
        for (var c = 1; c < names.Count; c++)
        {
            if (keys.Count == 0)
            {
                from.Append(" CROSS JOIN ").Append(SqlRenderer.Quote(names[c]));
                continue;
            }

            var on = Enumerable.Range(0, keys.Count).Select(k =>
            {
                var left = MergedKey(k, c - 1);
                var right = KeyOf(names[c], k);
                return $"({left} = {right} OR ({left} IS NULL AND {right} IS NULL))";
            });
            from.Append(" FULL OUTER JOIN ").Append(SqlRenderer.Quote(names[c])).Append(" ON ").Append(string.Join(" AND ", on));
        }

        var select = new List<string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case KeyItem key:
                    select.Add($"{MergedKey(keys.ToList().IndexOf(key), names.Count - 1)} AS {SqlRenderer.Quote(key.Alias)}");
                    break;
                case AggregateItem aggregate:
                {
                    var context = contexts.ToList().IndexOf(aggregate.Context);
                    var position = aggregates.Where(a => a.Context == aggregate.Context).ToList().IndexOf(aggregate);
                    select.Add($"{SqlRenderer.Quote(names[context])}.{SqlRenderer.Quote("a" + position.ToString(CultureInfo.InvariantCulture))} AS {SqlRenderer.Quote(aggregate.Alias)}");
                    break;
                }
            }
        }

        return "WITH " + string.Join(",\n", ctes) + "\nSELECT " + string.Join(", ", select) + "\nFROM " + from;
    }
}
