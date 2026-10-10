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
/// extractor cannot place stays unresolved and falls back to the same fail-closed fan-out.
/// </summary>
public static class SqlColumnAccessExtractor
{
    /// <summary>Extracts the (table, column) pairs a parsed SELECT statement exposes.</summary>
    public static IReadOnlyList<AccessedColumn> Extract(SelectStatement select)
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

    private sealed class Collector : TSqlFragmentVisitor
    {
        private readonly List<AccessedTable> _tables = [];
        private readonly Dictionary<string, AccessedTable> _aliases = new(StringComparer.OrdinalIgnoreCase);

        // Deferred until every table/alias in the statement has been seen: a column can appear in the AST
        // before the FROM clause that introduces its table (e.g. inside a CTE's own SELECT list, visited before
        // the outer query's alias is known in source order for some fragment shapes).
        private readonly List<(string? Qualifier, string? Column)> _references = [];

        public override void Visit(NamedTableReference node)
        {
            var name = node?.SchemaObject?.BaseIdentifier?.Value;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var table = new AccessedTable(node!.SchemaObject!.SchemaIdentifier?.Value, name);
            _tables.Add(table);

            var alias = node.Alias?.Value;
            _aliases[string.IsNullOrEmpty(alias) ? name : alias] = table;
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            var identifiers = node?.MultiPartIdentifier?.Identifiers;
            if (identifiers is null || identifiers.Count == 0)
            {
                return;
            }

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

        public IReadOnlyList<AccessedColumn> Resolve()
        {
            var result = new List<AccessedColumn>();
            foreach (var (qualifier, column) in _references)
            {
                if (qualifier is not null && _aliases.TryGetValue(qualifier, out var resolved))
                {
                    result.Add(new AccessedColumn(resolved, column));
                    continue;
                }

                // Unresolved qualifier (an alias this pass never saw) or none at all: fan out to every table,
                // per the fail-closed policy above.
                foreach (var table in _tables)
                {
                    result.Add(new AccessedColumn(table, column));
                }
            }

            return result;
        }
    }
}
