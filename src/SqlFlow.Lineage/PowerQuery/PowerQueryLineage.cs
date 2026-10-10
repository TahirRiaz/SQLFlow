using SqlFlow.Lineage.PowerBi;

namespace SqlFlow.Lineage.PowerQuery;

/// <summary>
/// Works out, for a Power Query table query, which source rows it produces and which T-SQL expression is behind each
/// of its columns: the base table its <c>Sql.Database</c> navigation reads, the queries it merges in, and what every
/// renaming, selecting, duplicating, adding, and expanding step made of the columns.
/// <para>
/// Only steps whose effect on the columns is exact are understood (selecting, removing, renaming, duplicating,
/// reordering, retyping, adding a computed column, and merging then expanding another query). A step that changes
/// WHICH rows the query produces (filtering, grouping, removing duplicates) or one this does not know refuses the
/// whole query, because a column mapping over the wrong rows would answer a different question. An added column
/// whose expression is outside the understood set refuses only that column.
/// </para>
/// </summary>
internal static class PowerQueryLineage
{
    /// <summary>How deep query references may chain (a table merging a query merging another).</summary>
    private const int MaxReferenceDepth = 16;

    /// <summary>
    /// Resolves the query <paramref name="m"/>. <paramref name="queries"/> maps every other query name the model
    /// holds (its shared expressions and its tables) to its M, for a merge to follow.
    /// </summary>
    public static Relation Resolve(string m, IReadOnlyDictionary<string, string> queries)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(queries);
        return new Evaluator(queries).ResolveQuery(m, new HashSet<string>(StringComparer.Ordinal), null);
    }

    private abstract record Value;

    /// <summary>A table value: its relation, the merged columns not yet expanded (name to join index), and the joins
    /// whose columns were expanded into the output.</summary>
    private sealed record RelationValue(Relation Relation, IReadOnlyDictionary<string, int> Nested, IReadOnlySet<int> Expanded) : Value
    {
        public static RelationValue Of(Relation relation)
            => new(relation, new Dictionary<string, int>(StringComparer.Ordinal), new HashSet<int>());
    }

    private sealed record DatabaseValue(string Database) : Value;

    private sealed record SchemaValue(string Database, string Schema) : Value;

    private sealed record ConstantValue(MExpr Expression) : Value;

    private sealed record RefusedValue(string Reason) : Value;

    private sealed class Evaluator(IReadOnlyDictionary<string, string> queries)
    {
        public Relation ResolveQuery(string m, HashSet<string> referencing, string? name)
        {
            if (referencing.Count > MaxReferenceDepth)
            {
                return Relation.Refused("queries reference each other more deeply than supported");
            }

            MExpr expression;
            try
            {
                expression = MParser.Parse(m);
            }
            catch (MParseException ex)
            {
                return Relation.Refused(
                    name is null ? $"its Power Query does not parse ({ex.Message})" : $"query '{name}' does not parse ({ex.Message})");
            }

            var value = Evaluate(expression, new Dictionary<string, Value>(StringComparer.Ordinal), referencing);
            return Finish(value, name);
        }

        private static Relation Finish(Value value, string? name)
        {
            var subject = name is null ? "its Power Query" : $"query '{name}'";
            switch (value)
            {
                case RelationValue relationValue:
                {
                    var relation = relationValue.Relation;
                    if (relation.Problem is not null)
                    {
                        return relation;
                    }

                    // A merge whose table was never expanded adds nothing to a left join's rows, so it is dropped; an
                    // unexpanded inner merge still filters rows, which nothing in the output would show.
                    var joins = new List<RelationJoin>();
                    for (var i = 0; i < relation.Joins.Count; i++)
                    {
                        if (relationValue.Expanded.Contains(i))
                        {
                            joins.Add(relation.Joins[i]);
                        }
                        else if (relation.Joins[i].Inner)
                        {
                            return Relation.Refused(
                                $"{subject} keeps an inner merge whose table it never expands, which filters rows");
                        }
                    }

                    return relation with { Joins = joins };
                }

                case RefusedValue refused:
                    return Relation.Refused(refused.Reason);
                default:
                    return Relation.Refused($"{subject} does not produce a table from a SQL Server database");
            }
        }

        private Value Evaluate(MExpr expression, Dictionary<string, Value> scope, HashSet<string> referencing)
        {
            switch (expression)
            {
                case MLet let:
                {
                    var inner = new Dictionary<string, Value>(scope, StringComparer.Ordinal);
                    foreach (var (stepName, stepValue) in let.Steps)
                    {
                        inner[stepName] = Evaluate(stepValue, inner, referencing);
                    }

                    return Evaluate(let.Body, inner, referencing);
                }

                case MIdentifier identifier:
                    if (scope.TryGetValue(identifier.Name, out var bound))
                    {
                        return bound;
                    }

                    return Reference(identifier.Name, referencing);

                case MCall { Target: MIdentifier function } call:
                    return Call(function.Name, call.Arguments, scope, referencing);

                case MFieldAccess { Field: "Data", Target: MItemAccess { Selector: MRecord selector } access }:
                    return Navigate(Evaluate(access.Target, scope, referencing), selector);

                case MText or MNumber or MKeywordLiteral or MList or MRecord or MEach or MType:
                    return new ConstantValue(expression);

                default:
                    return new RefusedValue($"the step '{Describe(expression)}' is not one this translation reads");
            }
        }

        private Value Reference(string name, HashSet<string> referencing)
        {
            if (!queries.TryGetValue(name, out var m))
            {
                return new RefusedValue($"it refers to '{name}', which is not a query the model holds");
            }

            if (!referencing.Add(name))
            {
                return new RefusedValue($"query '{name}' refers back to itself");
            }

            try
            {
                var relation = ResolveQuery(m, referencing, name);
                return relation.Problem is null
                    ? RelationValue.Of(relation)
                    : new RefusedValue(relation.Problem);
            }
            finally
            {
                referencing.Remove(name);
            }
        }

        private static Value Navigate(Value source, MRecord selector)
        {
            string? Field(params string[] names)
                => selector.Fields.FirstOrDefault(f => names.Contains(f.Name)).Value is MText text ? text.Value : null;

            switch (source)
            {
                case DatabaseValue database when Field("Schema") is { } schema && Field("Item") is { } item:
                    return OpenTable(database.Database, schema, item);
                case DatabaseValue database when Field("Name") is { } schemaName && Field("Item") is null:
                    return new SchemaValue(database.Database, schemaName);
                case SchemaValue schemaValue when Field("Name", "Item") is { } table:
                    return OpenTable(schemaValue.Database, schemaValue.Schema, table);
                case RefusedValue refused:
                    return refused;
                default:
                    return new RefusedValue("it navigates to its table in a way this translation does not read");
            }
        }

        private static RelationValue OpenTable(string database, string schema, string name)
            => RelationValue.Of(new Relation { Base = new SourceTable(database, schema, name), Open = true });

        private Value Call(string function, IReadOnlyList<MExpr> arguments, Dictionary<string, Value> scope, HashSet<string> referencing)
        {
            if (function == "Sql.Database")
            {
                if (arguments.Count < 2 || arguments[1] is not MText database)
                {
                    return new RefusedValue("its Sql.Database call does not name a database");
                }

                if (arguments.Count > 2 && arguments[2] is MRecord options && options.Fields.Any(f => f.Name == "Query"))
                {
                    return new RefusedValue("it runs a native SQL query, which this translation does not read");
                }

                return new DatabaseValue(database.Value);
            }

            if (!function.StartsWith("Table.", StringComparison.Ordinal))
            {
                return new RefusedValue($"it calls {function}, which this translation does not read");
            }

            if (arguments.Count == 0)
            {
                return new RefusedValue($"its {function} step has no table");
            }

            var input = Evaluate(arguments[0], scope, referencing);
            if (input is RefusedValue)
            {
                return input;
            }

            if (input is not RelationValue table)
            {
                return new RefusedValue($"its {function} step does not start from a table");
            }

            if (table.Relation.Problem is not null)
            {
                return table;
            }

            try
            {
                return function switch
                {
                    "Table.SelectColumns" => SelectColumns(table, arguments),
                    "Table.RemoveColumns" => RemoveColumns(table, arguments),
                    "Table.RenameColumns" => RenameColumns(table, arguments),
                    "Table.DuplicateColumn" => DuplicateColumn(table, arguments),
                    "Table.ReorderColumns" or "Table.TransformColumnTypes" => table,
                    "Table.AddColumn" => AddColumn(table, arguments),
                    "Table.NestedJoin" => NestedJoin(table, arguments, scope, referencing),
                    "Table.ExpandTableColumn" => ExpandTableColumn(table, arguments),
                    _ => new RefusedValue(RowChanging.Contains(function)
                        ? $"its {function} step changes which rows it produces"
                        : $"its {function} step is not one this translation reads"),
                };
            }
            catch (MParseException ex)
            {
                return new RefusedValue($"its {function} step is not in a form this translation reads ({ex.Message})");
            }
        }

        private static readonly HashSet<string> RowChanging = new(StringComparer.Ordinal)
        {
            "Table.SelectRows", "Table.Distinct", "Table.Group", "Table.FirstN", "Table.LastN", "Table.Skip",
            "Table.RemoveRows", "Table.RemoveFirstN", "Table.RemoveLastN", "Table.Combine", "Table.Join",
            "Table.ExpandListColumn", "Table.UnpivotOtherColumns", "Table.Unpivot", "Table.Pivot", "Table.Sort",
            "Table.RemoveMatchingRows", "Table.AlternateRows", "Table.Range", "Table.Repeat", "Table.FuzzyNestedJoin",
        };

        private static RelationValue SelectColumns(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            var names = Names(Argument(arguments, 1));
            var ignoreMissing = arguments.Count > 2 && arguments[2] is MIdentifier { Name: "MissingField.Ignore" or "MissingField.UseNull" };
            var relation = table.Relation;
            var columns = new List<KeyValuePair<string, ColumnValue>>();
            var nested = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (table.Nested.TryGetValue(name, out var joinIndex))
                {
                    nested[name] = joinIndex;
                    continue;
                }

                if (!relation.HasColumn(name))
                {
                    if (ignoreMissing)
                    {
                        continue;
                    }

                    throw new MParseException($"it selects '{name}', which the table does not have");
                }

                columns.Add(new(name, relation.Column(name)));
            }

            return table with { Relation = relation with { Columns = columns, Open = false }, Nested = nested };
        }

        private static RelationValue RemoveColumns(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            var names = new HashSet<string>(Names(Argument(arguments, 1)), StringComparer.Ordinal);
            var relation = table.Relation;
            var removed = new HashSet<string>(relation.Removed, StringComparer.Ordinal);
            removed.UnionWith(names);
            return table with
            {
                Relation = relation with
                {
                    Columns = relation.Columns.Where(c => !names.Contains(c.Key)).ToList(),
                    Removed = removed,
                },
                Nested = table.Nested.Where(n => !names.Contains(n.Key)).ToDictionary(StringComparer.Ordinal),
            };
        }

        private static RelationValue RenameColumns(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            var pairs = Argument(arguments, 1) is MList list && list.Items.All(i => i is MList)
                ? list.Items.Cast<MList>().ToList()
                : Argument(arguments, 1) is MList single ? [single] : throw new MParseException("the renames are not a list");
            var relation = table.Relation;
            var nested = table.Nested.ToDictionary(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                if (pair.Items.Count != 2 || pair.Items[0] is not MText from || pair.Items[1] is not MText to)
                {
                    throw new MParseException("a rename is not an {old, new} pair of names");
                }

                if (nested.Remove(from.Value, out var joinIndex))
                {
                    nested[to.Value] = joinIndex;
                    continue;
                }

                var value = relation.Column(from.Value);
                var removed = new HashSet<string>(relation.Removed, StringComparer.Ordinal) { from.Value };
                relation = relation with
                {
                    Columns = relation.Columns.Where(c => c.Key != from.Value)
                        .Append(new(to.Value, value))
                        .ToList(),
                    Removed = removed,
                };
            }

            return table with { Relation = relation, Nested = nested };
        }

        private static RelationValue DuplicateColumn(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            if (Argument(arguments, 1) is not MText from || Argument(arguments, 2) is not MText to)
            {
                throw new MParseException("the duplicated column is not named");
            }

            var relation = table.Relation;
            return table with
            {
                Relation = relation with { Columns = relation.Columns.Append(new(to.Value, relation.Column(from.Value))).ToList() },
            };
        }

        private static RelationValue AddColumn(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            if (Argument(arguments, 1) is not MText name)
            {
                throw new MParseException("the added column is not named");
            }

            var body = Argument(arguments, 2) switch
            {
                MEach each => each.Body,
                MFunction { Parameters.Count: 1 } function => Rebind(function.Body, function.Parameters[0]),
                _ => null,
            };
            var relation = table.Relation;
            var value = body is null
                ? ColumnValue.Refused($"column '{name.Value}' is added by a function this translation does not read")
                : Scalar(body, relation, name.Value);
            return table with { Relation = relation with { Columns = relation.Columns.Append(new(name.Value, value)).ToList() } };
        }

        // (row) => row[Col] means the same as each [Col].
        private static MExpr Rebind(MExpr body, string parameter) => body switch
        {
            MFieldAccess { Target: MIdentifier identifier } access when identifier.Name == parameter
                => new MFieldReference(access.Field),
            MBinary binary => binary with { Left = Rebind(binary.Left, parameter), Right = Rebind(binary.Right, parameter) },
            MUnary unary => unary with { Operand = Rebind(unary.Operand, parameter) },
            MCall call => call with { Arguments = call.Arguments.Select(a => Rebind(a, parameter)).ToList() },
            _ => body,
        };

        private Value NestedJoin(RelationValue table, IReadOnlyList<MExpr> arguments, Dictionary<string, Value> scope, HashSet<string> referencing)
        {
            var leftKeys = Names(Argument(arguments, 1));
            var right = Evaluate(Argument(arguments, 2), scope, referencing);
            var rightKeys = Names(Argument(arguments, 3));
            if (Argument(arguments, 4) is not MText newName)
            {
                throw new MParseException("the merged column is not named");
            }

            var kind = arguments.Count > 5 ? arguments[5] : null;
            var inner = kind switch
            {
                null or MIdentifier { Name: "JoinKind.LeftOuter" } => false,
                MIdentifier { Name: "JoinKind.Inner" } => true,
                MIdentifier other => throw new MParseException($"it merges with {other.Name}, which changes rows in a way this translation does not express"),
                _ => throw new MParseException("the merge kind is not a JoinKind"),
            };

            if (right is RefusedValue refused)
            {
                return refused;
            }

            if (right is not RelationValue rightTable || rightTable.Relation.Problem is not null)
            {
                return new RefusedValue("it merges with something that is not a table from a SQL Server database");
            }

            if (leftKeys.Count == 0 || leftKeys.Count != rightKeys.Count)
            {
                throw new MParseException("the merge keys do not pair up");
            }

            var on = new List<(SqlScalar, SqlScalar)>();
            for (var i = 0; i < leftKeys.Count; i++)
            {
                var left = table.Relation.Column(leftKeys[i]);
                var rightKey = rightTable.Relation.Column(rightKeys[i]);
                if (left.Expression is null || rightKey.Expression is null)
                {
                    return new RefusedValue(
                        $"its merge key '{leftKeys[i]}' = '{rightKeys[i]}' cannot be expressed ({left.Problem ?? rightKey.Problem})");
                }

                on.Add((left.Expression, rightKey.Expression));
            }

            var relation = table.Relation;
            var joins = relation.Joins.Append(new RelationJoin(inner, rightTable.Relation, on)).ToList();
            var nested = table.Nested.ToDictionary(StringComparer.Ordinal);
            nested[newName.Value] = joins.Count - 1;
            return table with { Relation = relation with { Joins = joins }, Nested = nested };
        }

        private static RelationValue ExpandTableColumn(RelationValue table, IReadOnlyList<MExpr> arguments)
        {
            if (Argument(arguments, 1) is not MText nestedName || !table.Nested.TryGetValue(nestedName.Value, out var joinIndex))
            {
                throw new MParseException("it expands a column that is not a merged table");
            }

            var columns = Names(Argument(arguments, 2));
            var newNames = arguments.Count > 3 ? Names(arguments[3]) : columns;
            if (newNames.Count != columns.Count)
            {
                throw new MParseException("the expanded columns and their new names do not pair up");
            }

            var relation = table.Relation;
            var right = relation.Joins[joinIndex].Right;
            var added = relation.Columns.ToList();
            for (var i = 0; i < columns.Count; i++)
            {
                added.Add(new(newNames[i], right.Column(columns[i])));
            }

            return new RelationValue(
                relation with { Columns = added },
                table.Nested.Where(n => n.Key != nestedName.Value).ToDictionary(StringComparer.Ordinal),
                new HashSet<int>(table.Expanded) { joinIndex });
        }

        private static ColumnValue Scalar(MExpr body, Relation relation, string column)
        {
            try
            {
                return ColumnValue.Of(ToScalar(body, relation));
            }
            catch (MParseException ex)
            {
                return ColumnValue.Refused($"column '{column}' is computed by {ex.Message}");
            }
        }

        private static SqlScalar ToScalar(MExpr expression, Relation relation)
        {
            switch (expression)
            {
                case MFieldReference field:
                case MFieldAccess { Target: MIdentifier { Name: "_" } }:
                {
                    var name = expression is MFieldReference reference ? reference.Field : ((MFieldAccess)expression).Field;
                    var value = relation.Column(name);
                    return value.Expression ?? throw new MParseException($"'{name}', which {value.Problem}");
                }

                case MText text:
                    return new SqlText(text.Value);
                case MNumber number:
                    return new SqlNumber(MParser.NumberLiteral(number.Value)
                        ?? throw new MParseException($"the number {number.Value}"));
                case MKeywordLiteral { Keyword: "null" }:
                    return new SqlNull();
                case MBinary { Operator: "&" } concat:
                    return new SqlConcat(Flatten(concat, relation).ToList());
                case MBinary { Operator: "+" or "-" or "*" or "/" } arithmetic:
                    return new SqlArithmetic(arithmetic.Operator, ToScalar(arithmetic.Left, relation), ToScalar(arithmetic.Right, relation));
                case MUnary { Operator: "-" } negate:
                    return new SqlNegate(ToScalar(negate.Operand, relation));
                case MUnary { Operator: "+" } plus:
                    return ToScalar(plus.Operand, relation);
                case MCall { Target: MIdentifier { Name: "Text.From" or "Number.ToText" }, Arguments.Count: >= 1 } toText:
                    return new SqlCast(ToScalar(toText.Arguments[0], relation), "nvarchar(4000)");
                case MCall { Target: MIdentifier { Name: "Number.From" }, Arguments.Count: >= 1 } toNumber:
                    return new SqlCast(ToScalar(toNumber.Arguments[0], relation), "float");
                case MCall { Target: MIdentifier { Name: "Int64.From" }, Arguments.Count: >= 1 } toInteger:
                    return new SqlCast(ToScalar(toInteger.Arguments[0], relation), "bigint");
                default:
                    throw new MParseException($"'{Describe(expression)}', which this translation does not read");
            }
        }

        private static IEnumerable<SqlScalar> Flatten(MExpr expression, Relation relation)
        {
            if (expression is MBinary { Operator: "&" } concat)
            {
                return Flatten(concat.Left, relation).Concat(Flatten(concat.Right, relation));
            }

            return [ToScalar(expression, relation)];
        }

        private static MExpr Argument(IReadOnlyList<MExpr> arguments, int index)
            => index < arguments.Count ? arguments[index] : throw new MParseException($"argument {index + 1} is missing");

        // A column argument is one name or a list of names.
        private static List<string> Names(MExpr expression) => expression switch
        {
            MText text => [text.Value],
            MList list when list.Items.All(i => i is MText) => list.Items.Cast<MText>().Select(t => t.Value).ToList(),
            _ => throw new MParseException("a column list is not a list of names"),
        };
    }

    private static string Describe(MExpr expression) => expression switch
    {
        MCall { Target: MIdentifier function } => function.Name,
        MIdentifier identifier => identifier.Name,
        MIf => "if ... then ... else",
        MBinary binary => "the operator " + binary.Operator,
        _ => expression.GetType().Name.TrimStart('M').ToLowerInvariant(),
    };
}
