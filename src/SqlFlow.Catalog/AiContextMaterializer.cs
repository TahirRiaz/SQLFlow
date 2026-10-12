using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;

namespace SqlFlow.Catalog;

/// <summary>
/// The one writer of what an approved AI context serves: the semantic layer's per-column allow-list entries, table
/// annotations, measures, joins and example questions, each row marked with the context's name. An approval, a
/// revocation and a sync's narrowing all come here, so what an assistant is served is always exactly the effective
/// version of every approved context, and nothing else writes those rows.
/// <para>
/// It writes by difference: a row that already serves what the version says is left as it is, so a sync that changes
/// nothing writes nothing, and a saved answer keeps the id an assistant cites it by for as long as its context gives it.
/// </para>
/// </summary>
public static class AiContextMaterializer
{
    /// <summary>
    /// Serves <paramref name="effective"/> as context <paramref name="name"/> in place of what it served before; null
    /// serves nothing. A row written before contexts were files on one of its tables is replaced. A table, measure name or
    /// join another context already serves is refused, naming both, and nothing is written: each table belongs to one
    /// context. The caller owns the transaction, so a refusal leaves the catalog as it was.
    /// </summary>
    /// <param name="db">The catalog, inside the caller's transaction.</param>
    /// <param name="name">The context's name.</param>
    /// <param name="repoId">The repository defining it, recorded on the examples it serves.</param>
    /// <param name="effective">What to serve, or null.</param>
    /// <param name="approvedBy">Who approved the version, recorded on every row written.</param>
    /// <param name="nowUtc">When the rows are written.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task ApplyAsync(
        CatalogDbContext db, string name, Guid repoId, AiContextVersion? effective, string? approvedBy, DateTime nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (effective is null)
        {
            await RemoveAsync(db, name, ct).ConfigureAwait(false);
            return;
        }

        var keys = effective.Tables.Select(t => t.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        await RefuseTablesOfOtherContextsAsync(db, name, keys, ct).ConfigureAwait(false);

        var measures = effective.Tables
            .SelectMany(t => t.Measures.Select(m => (Table: t, Measure: m)))
            .DistinctBy(m => m.Measure.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var measureNames = measures.Select(m => m.Measure.Name).ToList();
        var takenMeasure = await db.SemanticMeasures.AsNoTracking()
            .Where(m => m.AiContext != null && m.AiContext != name && measureNames.Contains(m.Name))
            .Select(m => new { m.Name, m.AiContext })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (takenMeasure is not null)
        {
            throw new SqlFlowException(
                $"Measure '{takenMeasure.Name}' is already served by AI context '{takenMeasure.AiContext}'; a measure name is unique across "
                + "every context. Rename it in one of the two files.");
        }

        var joins = effective.Tables
            .SelectMany(t => t.Joins.Select(j => (
                Table: t, Join: j, Identity: SemanticRelationshipIdentity.Compute(t.ObjectKey, j.Columns, j.ToObjectKey, j.ToColumns))))
            .DistinctBy(j => j.Identity, StringComparer.Ordinal)
            .ToList();

        // A join declared from its other end is the same join, so both directions are checked.
        var identities = joins
            .SelectMany(j => new[]
            {
                j.Identity,
                SemanticRelationshipIdentity.Compute(j.Join.ToObjectKey, j.Join.ToColumns, j.Table.ObjectKey, j.Join.Columns),
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var takenJoin = await db.SemanticRelationships.AsNoTracking()
            .Where(r => r.AiContext != null && r.AiContext != name && identities.Contains(r.IdentityHash))
            .Select(r => new { r.FromObjectKey, r.ToObjectKey, r.AiContext })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (takenJoin is not null)
        {
            throw new SqlFlowException(
                $"The join between '{takenJoin.FromObjectKey}' and '{takenJoin.ToObjectKey}' is already served by AI context "
                + $"'{takenJoin.AiContext}'. Declare it in one of the two files.");
        }

        // What the GUI wrote before contexts were files, on a table this context now opens or under a measure name or
        // join it now serves, gives way to it.
        await db.ColumnPolicies.Where(p => p.AiContext == null && keys.Contains(p.ObjectKey))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticObjects.Where(o => o.AiContext == null && keys.Contains(o.ObjectKey))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticMeasures.Where(m => m.AiContext == null && (keys.Contains(m.ObjectKey) || measureNames.Contains(m.Name)))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticRelationships.Where(r => r.AiContext == null && (keys.Contains(r.FromObjectKey) || identities.Contains(r.IdentityHash)))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var stamp = new Stamp(approvedBy, nowUtc);
        await ServeColumnsAsync(db, name, effective, stamp, ct).ConfigureAwait(false);
        await ServeTablesAsync(db, name, effective, stamp, ct).ConfigureAwait(false);
        await ServeMeasuresAsync(db, name, measures, stamp, ct).ConfigureAwait(false);
        await ServeJoinsAsync(db, name, joins, stamp, ct).ConfigureAwait(false);
        await ServeExamplesAsync(db, name, repoId, effective, stamp, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task ServeColumnsAsync(
        CatalogDbContext db, string name, AiContextVersion effective, Stamp stamp, CancellationToken ct)
    {
        var served = await db.ColumnPolicies.AsTracking().Where(p => p.AiContext == name).ToListAsync(ct).ConfigureAwait(false);
        var byColumn = served.ToDictionary(p => ColumnKey(p.ObjectKey, p.ColumnName), StringComparer.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in effective.Tables)
        {
            foreach (var column in table.Columns.Where(c => wanted.Add(ColumnKey(table.ObjectKey, c.Name))))
            {
                var synonyms = JoinLines(column.Synonyms);
                if (!byColumn.TryGetValue(ColumnKey(table.ObjectKey, column.Name), out var row))
                {
                    db.ColumnPolicies.Add(new CatalogColumnPolicy
                    {
                        ObjectKey = table.ObjectKey,
                        ColumnName = column.Name,
                        IsAllowed = true,
                        Description = column.Description,
                        Synonyms = synonyms,
                        UpdatedBy = stamp.By,
                        UpdatedUtc = stamp.Utc,
                        AiContext = name,
                    });
                }
                else if (!row.IsAllowed || row.Reason is not null
                    || !string.Equals(row.ColumnName, column.Name, StringComparison.Ordinal)
                    || !string.Equals(row.Description, column.Description, StringComparison.Ordinal)
                    || !string.Equals(row.Synonyms, synonyms, StringComparison.Ordinal))
                {
                    row.ColumnName = column.Name;
                    row.IsAllowed = true;
                    row.Reason = null;
                    row.Description = column.Description;
                    row.Synonyms = synonyms;
                    row.UpdatedBy = stamp.By;
                    row.UpdatedUtc = stamp.Utc;
                }
            }
        }

        db.ColumnPolicies.RemoveRange(served.Where(p => !wanted.Contains(ColumnKey(p.ObjectKey, p.ColumnName))));
    }

    private static async Task ServeTablesAsync(
        CatalogDbContext db, string name, AiContextVersion effective, Stamp stamp, CancellationToken ct)
    {
        var served = await db.SemanticObjects.AsTracking().Where(o => o.AiContext == name).ToListAsync(ct).ConfigureAwait(false);
        var byKey = served.ToDictionary(o => o.ObjectKey, StringComparer.Ordinal);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in effective.Tables.Where(t => wanted.Add(t.ObjectKey)))
        {
            var synonyms = JoinLines(table.Synonyms);
            var key = table.Key.Count == 0 ? null : string.Join(',', table.Key);
            if (!byKey.TryGetValue(table.ObjectKey, out var row))
            {
                db.SemanticObjects.Add(new CatalogSemanticObject
                {
                    ObjectKey = table.ObjectKey,
                    BusinessName = table.BusinessName,
                    Description = table.Description,
                    Synonyms = synonyms,
                    KeyColumns = key,
                    UpdatedBy = stamp.By,
                    UpdatedUtc = stamp.Utc,
                    AiContext = name,
                });
            }
            else if (!string.Equals(row.BusinessName, table.BusinessName, StringComparison.Ordinal)
                || !string.Equals(row.Description, table.Description, StringComparison.Ordinal)
                || !string.Equals(row.Synonyms, synonyms, StringComparison.Ordinal)
                || !string.Equals(row.KeyColumns, key, StringComparison.Ordinal))
            {
                row.BusinessName = table.BusinessName;
                row.Description = table.Description;
                row.Synonyms = synonyms;
                row.KeyColumns = key;
                row.UpdatedBy = stamp.By;
                row.UpdatedUtc = stamp.Utc;
            }
        }

        db.SemanticObjects.RemoveRange(served.Where(o => !wanted.Contains(o.ObjectKey)));
    }

    private static async Task ServeMeasuresAsync(
        CatalogDbContext db, string name, IReadOnlyList<(AiContextVersionTable Table, AiContextVersionMeasure Measure)> measures,
        Stamp stamp, CancellationToken ct)
    {
        var served = await db.SemanticMeasures.AsTracking().Where(m => m.AiContext == name).ToListAsync(ct).ConfigureAwait(false);
        var byName = served.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (table, measure) in measures)
        {
            if (!byName.TryGetValue(measure.Name, out var row))
            {
                db.SemanticMeasures.Add(new CatalogSemanticMeasure
                {
                    Name = measure.Name,
                    ObjectKey = table.ObjectKey,
                    Expression = measure.Expression,
                    Description = measure.Description,
                    UpdatedBy = stamp.By,
                    UpdatedUtc = stamp.Utc,
                    AiContext = name,
                });
            }
            else if (!string.Equals(row.Name, measure.Name, StringComparison.Ordinal)
                || !string.Equals(row.ObjectKey, table.ObjectKey, StringComparison.Ordinal)
                || !string.Equals(row.Expression, measure.Expression, StringComparison.Ordinal)
                || !string.Equals(row.Description, measure.Description, StringComparison.Ordinal))
            {
                row.Name = measure.Name;
                row.ObjectKey = table.ObjectKey;
                row.Expression = measure.Expression;
                row.Description = measure.Description;
                row.UpdatedBy = stamp.By;
                row.UpdatedUtc = stamp.Utc;
            }
        }

        var wanted = measures.Select(m => m.Measure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.SemanticMeasures.RemoveRange(served.Where(m => !wanted.Contains(m.Name)));
    }

    private static async Task ServeJoinsAsync(
        CatalogDbContext db, string name,
        IReadOnlyList<(AiContextVersionTable Table, AiContextVersionJoin Join, string Identity)> joins, Stamp stamp,
        CancellationToken ct)
    {
        var served = await db.SemanticRelationships.AsTracking().Where(r => r.AiContext == name).ToListAsync(ct).ConfigureAwait(false);
        var byIdentity = served.ToDictionary(r => r.IdentityHash, StringComparer.Ordinal);
        foreach (var (table, join, identity) in joins)
        {
            var fromColumns = string.Join(',', join.Columns);
            var toColumns = string.Join(',', join.ToColumns);
            var joinType = SemanticJoinType.Normalize(join.JoinType) ?? SemanticJoinType.Inner;
            if (!byIdentity.TryGetValue(identity, out var row))
            {
                db.SemanticRelationships.Add(new CatalogSemanticRelationship
                {
                    FromObjectKey = table.ObjectKey,
                    FromColumns = fromColumns,
                    ToObjectKey = join.ToObjectKey,
                    ToColumns = toColumns,
                    JoinType = joinType,
                    Description = join.Description,
                    IdentityHash = identity,
                    UpdatedBy = stamp.By,
                    UpdatedUtc = stamp.Utc,
                    AiContext = name,
                });
            }
            else if (!string.Equals(row.FromColumns, fromColumns, StringComparison.Ordinal)
                || !string.Equals(row.ToColumns, toColumns, StringComparison.Ordinal)
                || !string.Equals(row.JoinType, joinType, StringComparison.Ordinal)
                || !string.Equals(row.Description, join.Description, StringComparison.Ordinal))
            {
                row.FromColumns = fromColumns;
                row.ToColumns = toColumns;
                row.JoinType = joinType;
                row.Description = join.Description;
                row.UpdatedBy = stamp.By;
                row.UpdatedUtc = stamp.Utc;
            }
        }

        var wanted = joins.Select(j => j.Identity).ToHashSet(StringComparer.Ordinal);
        db.SemanticRelationships.RemoveRange(served.Where(r => !wanted.Contains(r.IdentityHash)));
    }

    /// <summary>
    /// The context's example questions. An example someone already confirmed, a report gave, or another context gives
    /// stays as it is: it already serves the same knowledge, and removing this context must not take it away.
    /// </summary>
    private static async Task ServeExamplesAsync(
        CatalogDbContext db, string name, Guid repoId, AiContextVersion effective, Stamp stamp, CancellationToken ct)
    {
        var served = await db.SemanticExamples.AsTracking().Where(e => e.AiContext == name).ToListAsync(ct).ConfigureAwait(false);
        var byHash = served.ToDictionary(e => e.ContentHash, StringComparer.Ordinal);
        var wanted = effective.Examples
            .Select(e => (Example: e, Hash: SemanticExampleHash.Compute(e.Question, e.Sql)))
            .DistinctBy(e => e.Hash, StringComparer.Ordinal)
            .ToList();
        var missing = wanted.Where(w => !byHash.ContainsKey(w.Hash)).Select(w => w.Hash).ToList();
        var heldElsewhere = missing.Count == 0
            ? []
            : (await db.SemanticExamples.AsNoTracking()
                    .Where(e => missing.Contains(e.ContentHash))
                    .Select(e => e.ContentHash)
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

        foreach (var (example, hash) in wanted)
        {
            var objectKeys = string.Join('\n', example.ObjectKeys);
            if (byHash.TryGetValue(hash, out var row))
            {
                if (!string.Equals(row.ObjectKeys, objectKeys, StringComparison.Ordinal)
                    || !string.Equals(row.SourceRef, example.SourceRef, StringComparison.Ordinal)
                    || row.RepoId != repoId)
                {
                    row.ObjectKeys = objectKeys;
                    row.SourceRef = example.SourceRef;
                    row.RepoId = repoId;
                }
            }
            else if (!heldElsewhere.Contains(hash))
            {
                db.SemanticExamples.Add(new CatalogSemanticExample
                {
                    RepoId = repoId,
                    Question = example.Question,
                    Sql = example.Sql,
                    ObjectKeys = objectKeys,
                    Provenance = SemanticExampleProvenance.AiContext,
                    ConfirmedUtc = stamp.Utc,
                    ConfirmedBy = stamp.By,
                    ContentHash = hash,
                    SourceRef = example.SourceRef,
                    AiContext = name,
                });
            }
        }

        var kept = wanted.Select(w => w.Hash).ToHashSet(StringComparer.Ordinal);
        db.SemanticExamples.RemoveRange(served.Where(e => !kept.Contains(e.ContentHash)));
    }

    /// <summary>Removes everything context <paramref name="name"/> serves.</summary>
    public static async Task RemoveAsync(CatalogDbContext db, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.ColumnPolicies.Where(p => p.AiContext == name).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticObjects.Where(o => o.AiContext == name).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticMeasures.Where(m => m.AiContext == name).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticRelationships.Where(r => r.AiContext == name).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.SemanticExamples.Where(e => e.AiContext == name).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The table, of <paramref name="keys"/>, another approved context already serves, with that context's
    /// name; null when none is taken.</summary>
    public static async Task<(string ObjectKey, string Context)?> TableOfOtherContextAsync(
        CatalogDbContext db, string name, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.ToList();
        var taken = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.AiContext != null && p.AiContext != name && list.Contains(p.ObjectKey))
            .Select(p => new { p.ObjectKey, p.AiContext })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return taken is null ? null : (taken.ObjectKey, taken.AiContext!);
    }

    private static async Task RefuseTablesOfOtherContextsAsync(
        CatalogDbContext db, string name, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (await TableOfOtherContextAsync(db, name, keys, ct).ConfigureAwait(false) is { } taken)
        {
            throw new SqlFlowException(
                $"Table '{taken.ObjectKey}' is already in AI context '{taken.Context}'; a table belongs to one context. Remove it "
                + "from one of the two files, or revoke the other context first.");
        }
    }

    private static string? JoinLines(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join('\n', values);

    /// <summary>A column's identity among the rows of one context: the catalog's unique key on (object, column) does not
    /// distinguish case, so neither does this.</summary>
    private static string ColumnKey(string objectKey, string column) => objectKey + "\n" + column.ToUpperInvariant();

    /// <summary>Who writes a row and when.</summary>
    private sealed record Stamp(string? By, DateTime Utc);
}
