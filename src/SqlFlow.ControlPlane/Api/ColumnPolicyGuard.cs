using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The data-scope half of the ad-hoc query trust boundary: where <see cref="ReadOnlyQueryGuard"/> proves a
/// statement is a single read-only SELECT, this proves it reads only columns an admin has explicitly allow-listed
/// (see <see cref="CatalogColumnPolicy"/>). Both must pass before a statement may be prepared, confirmed as a
/// PowerAI example, or auto-run, so a query built by an LLM cannot reach a denied column by guessing or by an
/// old stored example. This is the last line rather than the only one: the semantic layer and the column search
/// both withhold denied column names from an assistant composing SQL. They do not withhold them on every
/// surface, though - a definition search returns CREATE TABLE text and module bodies, which name every column a
/// table has - so what a caller could or could not see is never assumed here; the statement itself is checked.
///
/// The model is default-deny, at two levels. First, a table reference the catalog has no object for at all - a
/// synonym, or anything else not harvested into the catalog - is refused outright: with no catalog object there
/// is no allow-list to check it against, so it cannot be let through unchecked the way a blacklist would. Second,
/// for a table the catalog DOES know, only a column with an explicit <see cref="CatalogColumnPolicy.IsAllowed"/>
/// row may be read; a column with no row, or an explicitly denied one, is refused. A common table expression's
/// own name is exempted from the first check (it is not a catalog object and never will be), but every real
/// table its body reads is still checked normally.
///
/// Table references are resolved to catalog objects by schema/name only (matching case-insensitively; an
/// unqualified reference matches any schema), never by the connection a query happens to run against, because a
/// policy is set once per object and must hold regardless of which datasource reference reaches it.
/// </summary>
public static class ColumnPolicyGuard
{
    /// <summary>
    /// Throws <see cref="SqlFlowException"/> naming the offending table or column when <paramref name="sql"/> (a
    /// statement <see cref="ReadOnlyQueryGuard"/> has already accepted) reads something not on the allow-list. A
    /// statement this parses differently than the guard already did passes: this is a second, narrower check
    /// layered on the guard's own parse, not a replacement for it.
    /// </summary>
    public static async Task EnsureAllowedAsync(CatalogDbContext db, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var select = ParseSingleSelect(sql);
        if (select is null)
        {
            return;
        }

        var accesses = SqlColumnAccessExtractor.Extract(select);
        if (accesses.Count == 0)
        {
            return;
        }

        // A CTE's own name parses identically to a real table reference, so it is excluded here by name: it is
        // never a catalog object, and denying it would refuse every query that uses a CTE. The real tables its
        // body reads still appear as their own accesses and are checked normally.
        var cteNames = (select.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? [])
            .Select(c => c.ExpressionName.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tableNames = accesses.Select(a => a.Table.Name)
            .Where(name => !cteNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tableNames.Count == 0)
        {
            return;
        }

        var candidates = await db.Objects.AsNoTracking()
            .Where(o => tableNames.Contains(o.Name))
            .Select(o => new { o.Key, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Fail closed: a table name the catalog has no object for at all - a synonym is the case that motivated
        // this, but it applies to any object type the harvester does not track - has no allow-list to check, so
        // it is refused rather than passed through unchecked.
        foreach (var name in tableNames)
        {
            if (!candidates.Exists(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlFlowException(
                    $"'{name}' is not a table or view the catalog has a record of, so its column policy cannot " +
                    "be verified and it is refused. Sync the catalog first, or query a known object.");
            }
        }

        var candidateKeys = candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
        var allowed = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.IsAllowed && candidateKeys.Contains(p.ObjectKey))
            .Select(p => new { p.ObjectKey, p.ColumnName })
            .ToListAsync(ct).ConfigureAwait(false);

        var allowedByKey = allowed
            .GroupBy(p => p.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var objectColumns = await db.ObjectColumns.AsNoTracking()
            .Where(c => candidateKeys.Contains(c.ObjectKey))
            .Select(c => new { c.ObjectKey, c.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var columnsByKey = objectColumns
            .GroupBy(c => c.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).ToList());

        foreach (var access in accesses)
        {
            if (cteNames.Contains(access.Table.Name))
            {
                continue;
            }

            foreach (var candidate in candidates.Where(c => access.Table.Matches(c.Schema, c.Name)))
            {
                allowedByKey.TryGetValue(candidate.Key, out var allowedColumns);
                allowedColumns ??= [];

                if (access.Column is null)
                {
                    columnsByKey.TryGetValue(candidate.Key, out var knownColumns);
                    var notAllowed = (knownColumns ?? []).Where(c => !allowedColumns.Contains(c)).ToList();
                    if (notAllowed.Count > 0)
                    {
                        throw new SqlFlowException(
                            $"'{candidate.Schema}.{candidate.Name}' has columns not on the allow-list " +
                            $"({string.Join(", ", notAllowed)}), so SELECT * (or an unqualified/table-qualified " +
                            "star) cannot be used against it here. Name only the allowed columns.");
                    }

                    continue;
                }

                if (!allowedColumns.Contains(access.Column))
                {
                    throw new SqlFlowException(
                        $"Column '{candidate.Schema}.{candidate.Name}.{access.Column}' is not on the allow-list " +
                        "and cannot be read here.");
                }
            }
        }
    }

    /// <summary>
    /// The SELECT a data-operations task run by the assistant surface would read, so the task passes the same
    /// read-only and allow-list checks as an ad-hoc query. Throws <see cref="SqlFlowException"/> for a task the
    /// assistant may not run at all:
    /// <list type="bullet">
    /// <item>duplicateKeys must name its key columns: left empty it reads whatever key the table declares, which
    /// can be a column outside the semantic layer, and asks back with every candidate column's name.</item>
    /// <item>compareBaseline in inventory mode lists every object of a schema, tables outside the layer included.</item>
    /// <item>compareBaseline in schema mode reports every column, so it reads like <c>SELECT *</c>.</item>
    /// <item>compareBaseline in data mode reads its key expressions, its compare columns (every column when none are
    /// named, hence <c>*</c>), and its filter.</item>
    /// <item>Every other operation is refused: no assistant tool enqueues one.</item>
    /// </list>
    /// </summary>
    internal static string ComposeTaskSelect(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var target = string.IsNullOrWhiteSpace(payload.Schema) || string.IsNullOrWhiteSpace(payload.ObjectName)
            ? null
            : $"{SemanticLayer.QuoteIdentifier(payload.Schema.Trim())}.{SemanticLayer.QuoteIdentifier(payload.ObjectName.Trim())}";

        switch (payload.Operation)
        {
            case ComputeOperations.DuplicateKeys when target is not null:
                if (payload.Columns is not { Count: > 0 } keyColumns)
                {
                    throw new SqlFlowException(
                        "Name the key columns: the assistant's duplicate-key check must use the allowed key " +
                        "describe_semantic_table reports, since the key the table declares could be a column outside " +
                        "the semantic layer.");
                }

                return $"SELECT {string.Join(", ", keyColumns.Select(c => SemanticLayer.QuoteIdentifier(c.Trim())))} FROM {target}";

            case ComputeOperations.CompareBaseline when payload.CompareMode is BaselineCompareMode.Inventory:
                throw new SqlFlowException(
                    "An inventory comparison lists every object of a schema, including tables outside the semantic " +
                    "layer, so the assistant cannot run one. Compare one semantic layer table in schema or data mode.");

            case ComputeOperations.CompareBaseline when payload.CompareMode is BaselineCompareMode.Schema && target is not null:
                return $"SELECT * FROM {target}";

            case ComputeOperations.CompareBaseline when payload.CompareMode is BaselineCompareMode.Data && target is not null:
            {
                var selected = (payload.KeyExpressions ?? [])
                    .Concat(payload.CompareColumns is { Count: > 0 } compared
                        ? compared.Select(c => SemanticLayer.QuoteIdentifier(c.Trim()))
                        : ["*"])
                    .ToList();
                var sql = $"SELECT {string.Join(", ", selected)} FROM {target}";
                return string.IsNullOrWhiteSpace(payload.Where) ? sql : $"{sql} WHERE {payload.Where}";
            }

            default:
                throw new SqlFlowException(
                    $"The '{payload.Operation}' task is not available to the assistant with these arguments: it runs " +
                    "duplicateKeys and compareBaseline (schema or data mode) over one semantic layer table only.");
        }
    }

    /// <summary>Throws <see cref="SqlFlowException"/> unless the task the assistant surface asked for reads only
    /// allow-listed columns of catalogued objects (see <see cref="ComposeTaskSelect"/>).</summary>
    internal static async Task EnsureTaskAllowedAsync(CatalogDbContext db, ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var validated = ReadOnlyQueryGuard.Validate(ComposeTaskSelect(payload));
        if (ParseSingleSelect(validated) is null)
        {
            // EnsureAllowedAsync passes a statement it cannot parse as one SELECT, trusting ReadOnlyQueryGuard to have
            // refused it; a composed task statement gets no such benefit of the doubt.
            throw new SqlFlowException("The task's columns and filter could not be verified against the column allow-list.");
        }

        await EnsureAllowedAsync(db, validated, ct).ConfigureAwait(false);
    }

    /// <summary>Parses <paramref name="sql"/> as exactly one SELECT, or null when it is anything else. Shared with
    /// <see cref="DatasourceInference"/> so both read a statement's tables from the same parse.</summary>
    internal static SelectStatement? ParseSingleSelect(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            return null;
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        return statements is [SelectStatement select] ? select : null;
    }
}
