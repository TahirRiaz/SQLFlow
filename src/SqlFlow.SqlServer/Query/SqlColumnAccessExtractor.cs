using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlFlow.SqlServer.Query;

/// <summary>One table a parsed SELECT reads, as written: schema is null when the author left it off (resolved
/// against the caller's default schema, not here).</summary>
public sealed record AccessedTable(string? Schema, string Name)
{
    public bool Matches(string? schema, string name)
        => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
            && (Schema is null || string.Equals(Schema, schema, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One column reference a parsed SELECT makes against a table it reads. <see cref="Column"/> is null
/// for a <c>SELECT *</c> or <c>alias.*</c>: every column of <see cref="Table"/> is exposed, not just the ones
/// named elsewhere in the statement.</summary>
public sealed record AccessedColumn(AccessedTable Table, string? Column);

/// <summary>One column reference as written, with every table it may read: the one table (or several, for an alias
/// reused at another nesting level) its qualifier names, or every table the statement reads when it has no qualifier
/// or one this cannot place. <see cref="Column"/> is null for a star.</summary>
public sealed record AccessedReference(string? Column, IReadOnlyList<AccessedTable> Tables);

/// <summary>
/// Extracts, from an already-parsed read-only SELECT (see <see cref="ReadOnlyQueryGuard"/>), every (table,
/// column) pair the statement exposes, so a caller can check each one against a data-access policy before the
/// query is allowed to run.
///
/// This is deliberately coarse, matching <see cref="ReadOnlyQueryGuard"/>'s own stance: a column reference this
/// extractor cannot bind to one table with certainty (an unqualified column, or an alias it cannot resolve) is
/// attributed to EVERY table the statement reads rather than dropped or guessed at. A query spanning a policy
/// this extractor is unsure about must fail closed, not open; an over-broad refusal costs a rewrite, a missed
/// one costs data. Likewise, only a top-level alias map is built (FROM/JOIN of the outer query and of every
/// subquery/derived table the fragment contains, all at once, not scoped per nesting level): an alias this
/// extractor cannot place stays unresolved and falls back to the same fail-closed fan-out, and an alias given to
/// more than one table (the same letter in a query and in its subquery) is attributed to every table it names.
///
/// A table-valued function a query reads from (<c>FROM dbo.ufn_Orders()</c>) is a table here like any other, by its
/// schema and name, since what it returns is decided inside it. A call made on a column (<c>e.Doc.value(...)</c>,
/// which parses exactly like a schema-qualified function call <c>dbo.fn(...)</c>) reads that column, so its target is
/// reported as one; and the pseudo-columns <c>$IDENTITY</c> and <c>$ROWGUID</c> are reported under those names, which
/// no policy allows, rather than being taken for no column at all.
/// </summary>
public static class SqlColumnAccessExtractor
{
    /// <summary>The name a <c>$IDENTITY</c> pseudo-column is reported under.</summary>
    public const string IdentityPseudoColumn = "$IDENTITY";

    /// <summary>The name a <c>$ROWGUID</c> pseudo-column is reported under.</summary>
    public const string RowGuidPseudoColumn = "$ROWGUID";

    /// <summary>Extracts the (table, column) pairs a parsed SELECT statement exposes.</summary>
    public static IReadOnlyList<AccessedColumn> Extract(SelectStatement select)
    {
        ArgumentNullException.ThrowIfNull(select);

        return ExtractReferences(select)
            .SelectMany(reference => reference.Tables.Select(table => new AccessedColumn(table, reference.Column)))
            .Distinct()
            .ToList();
    }

    /// <summary>Each column reference a parsed SELECT makes, with every table it may read (see
    /// <see cref="AccessedReference"/>). A caller that must tell where a reference can resolve, rather than only which
    /// (table, column) pairs to check, reads this instead of <see cref="Extract"/>.</summary>
    public static IReadOnlyList<AccessedReference> ExtractReferences(SelectStatement select)
    {
        ArgumentNullException.ThrowIfNull(select);

        var collector = new Collector();
        select.Accept(collector);
        return collector.Resolve();
    }

    /// <summary>Every table a parsed SELECT reads, whether or not any column of it is referenced. A statement
    /// like <c>SELECT COUNT(*) FROM demo.Customers</c> names no column at all, so <see cref="Extract"/> reports
    /// nothing for it; a caller asking WHERE a query reads from, rather than which columns it exposes, needs
    /// this instead.</summary>
    public static IReadOnlyList<AccessedTable> ExtractTables(SelectStatement select)
    {
        ArgumentNullException.ThrowIfNull(select);

        var collector = new Collector();
        select.Accept(collector);
        return collector.Tables;
    }

    /// <summary>
    /// The columns a parsed SELECT names while reading no table at all (<c>SELECT dbo.fn(1)</c>, whose call target is
    /// read as a column), which <see cref="Extract"/> cannot attribute to anything. A caller enforcing a policy refuses
    /// a statement that has any, rather than let a reference it cannot check through.
    /// </summary>
    public static IReadOnlyList<string> ExtractUnattributed(SelectStatement select)
    {
        ArgumentNullException.ThrowIfNull(select);

        var collector = new Collector();
        select.Accept(collector);
        return collector.Unattributed;
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        private readonly List<AccessedTable> _tables = [];
        private readonly Dictionary<string, HashSet<AccessedTable>> _aliases = new(StringComparer.OrdinalIgnoreCase);

        // Deferred until every table/alias in the statement has been seen: a column can appear in the AST
        // before the FROM clause that introduces its table (e.g. inside a CTE's own SELECT list, visited before
        // the outer query's alias is known in source order for some fragment shapes).
        private readonly List<(string? Qualifier, string? Column)> _references = [];

        public override void Visit(NamedTableReference node)
            => AddTable(node?.SchemaObject, node?.Alias?.Value);

        public override void Visit(SchemaObjectFunctionTableReference node)
            => AddTable(node?.SchemaObject, node?.Alias?.Value);

        private void AddTable(SchemaObjectName? name, string? alias)
        {
            var baseName = name?.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(baseName))
            {
                return;
            }

            var table = new AccessedTable(name!.SchemaIdentifier?.Value, baseName);
            _tables.Add(table);

            var key = string.IsNullOrEmpty(alias) ? baseName : alias;
            if (!_aliases.TryGetValue(key, out var bound))
            {
                bound = [];
                _aliases[key] = bound;
            }

            bound.Add(table);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node is null)
            {
                return;
            }

            var identifiers = node.MultiPartIdentifier?.Identifiers;
            // IDENTITYCOL and $IDENTITY, ROWGUIDCOL and $ROWGUID, and the graph and change-tracking pseudo-columns
            // ($node_id, $action, ...) name a real column without writing its name; COUNT(*)'s star is no column.
            var pseudo = node.ColumnType switch
            {
                ColumnType.Regular or ColumnType.Wildcard => null,
                ColumnType.IdentityCol or ColumnType.PseudoColumnIdentity => IdentityPseudoColumn,
                ColumnType.RowGuidCol or ColumnType.PseudoColumnRowGuid => RowGuidPseudoColumn,
                var other => "$" + other.ToString().Replace("PseudoColumn", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
            };
            if (pseudo is not null)
            {
                _references.Add((identifiers is { Count: > 0 } ? identifiers[^1].Value : null, pseudo));
                return;
            }

            if (identifiers is null || identifiers.Count == 0)
            {
                return;
            }

            AddReference(identifiers);
        }

        public override void Visit(FunctionCall node)
        {
            // dbo.fn(x) and e.Doc.value(x) parse alike: the target is read as the column it may be. A schema read that
            // way is no column a policy allows, so a schema-qualified function call fails closed with the rest.
            if (node?.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } target })
            {
                AddReference(target);
            }
        }

        private void AddReference(IList<Identifier> identifiers)
        {
            var column = identifiers[^1].Value;
            var qualifier = identifiers.Count >= 2 ? identifiers[^2].Value : null;
            _references.Add((qualifier, column));
        }

        public override void Visit(SelectStarExpression node)
        {
            var qualifier = node?.Qualifier?.Identifiers is { Count: > 0 } parts ? parts[^1].Value : null;
            _references.Add((qualifier, null));
        }

        public IReadOnlyList<AccessedTable> Tables => _tables.Distinct().ToList();

        public IReadOnlyList<string> Unattributed => _tables.Count > 0
            ? []
            : _references.Select(r => r.Column ?? "*").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public IReadOnlyList<AccessedReference> Resolve()
        {
            var tables = _tables.Distinct().ToList();
            var result = new List<AccessedReference>();
            foreach (var (qualifier, column) in _references)
            {
                // An alias bound to several tables (reused at another nesting level) may read each of them; an
                // unresolved qualifier (an alias this pass never saw) or none at all may read any table, per the
                // fail-closed policy above.
                IReadOnlyList<AccessedTable> candidates = qualifier is not null && _aliases.TryGetValue(qualifier, out var resolved)
                    ? resolved.ToList()
                    : tables;
                if (candidates.Count > 0)
                {
                    result.Add(new AccessedReference(column, candidates));
                }
            }

            return result;
        }
    }
}
