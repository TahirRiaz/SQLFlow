using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.AiContext;
using SqlFlow.Core.Compute;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Where an AI context stands: <see cref="AiContextStatus"/>.</summary>
/// <param name="Name">The context's name.</param>
/// <param name="Description">What it covers, as its file says now.</param>
/// <param name="RepoId">The repository whose file defines it.</param>
/// <param name="RepoName">That repository's name.</param>
/// <param name="Path">The file, within the repository.</param>
/// <param name="Status">One of <see cref="AiContextStatus"/>.</param>
/// <param name="CurrentVersion">The version the file proposes (its hash), or null while the file does not read.</param>
/// <param name="ApprovedVersion">The version an admin approved, or null.</param>
/// <param name="ApprovedBy">Who approved it.</param>
/// <param name="ApprovedUtc">When.</param>
/// <param name="SyncedUtc">When a sync last read the file.</param>
/// <param name="TableCount">How many tables the file proposes.</param>
/// <param name="ServedTableCount">How many tables the context serves now.</param>
/// <param name="ProblemCount">How many problems keep the current version from being approved.</param>
public sealed record AiContextSummaryDto(
    string Name, string? Description, Guid RepoId, string RepoName, string Path, string Status,
    string? CurrentVersion, string? ApprovedVersion, string? ApprovedBy, DateTime? ApprovedUtc, DateTime SyncedUtc,
    int TableCount, int ServedTableCount, int ProblemCount);

/// <summary>Every AI context, and whether content written before contexts were files is waiting to be exported.</summary>
public sealed record AiContextListDto(IReadOnlyList<AiContextSummaryDto> Contexts, bool HasEarlierContent);

/// <summary>One difference between the approved version and the one the file proposes.</summary>
/// <param name="Kind"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
/// <param name="Subject">What changed, as a person reads it ("table dw.sales.Orders", "column Amount of ...").</param>
/// <param name="Detail">How, when there is more to say than the kind.</param>
public sealed record AiContextChangeDto(string Kind, string Subject, string? Detail);

/// <summary>One AI context in full: where it stands, the version its file proposes, the version approved, what approving
/// the proposed one would change, and why it cannot be approved when it cannot.</summary>
public sealed record AiContextDetailDto(
    AiContextSummaryDto Summary,
    AiContextVersion? Current,
    AiContextVersion? Approved,
    IReadOnlyList<AiContextChangeDto> Changes,
    IReadOnlyList<string> Problems);

/// <summary>A context's file as text: as the last sync read it, and as it was when the approved version was approved,
/// each credential-redacted as a flow's YAML is.</summary>
/// <param name="Path">The file, within its repository.</param>
/// <param name="Current">The file's text now, or null when it could not be read.</param>
/// <param name="Approved">The file's text when the approved version was approved, or null while none is.</param>
public sealed record AiContextYamlDto(string Path, string? Current, string? Approved);

/// <summary>Approves the version an admin reviewed: <see cref="Version"/> must be the context's current version, so a
/// file that changed while the admin was reading is never approved unseen.</summary>
public sealed record ApproveAiContextRequest(string? Version);

/// <summary>The states an AI context can be in.</summary>
public static class AiContextStatus
{
    /// <summary>Never approved; the current version can be.</summary>
    public const string AwaitingApproval = "awaiting-approval";

    /// <summary>The approved version is the one the file proposes.</summary>
    public const string Approved = "approved";

    /// <summary>The file proposes a version other than the approved one; the approved one is served, narrowed by the file.</summary>
    public const string Changed = "changed";

    /// <summary>The file's current version has problems and cannot be approved. An approved version is still served.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// The AI contexts files propose, for admins: every context and where it stands, one context in full with what approving
/// it would change, its file as text (now and as approved), approving the version an admin reviewed, revoking an approval, and the export of what was typed in the
/// GUI before contexts were files. The admin scope throughout: deciding what an AI assistant may use is a governance
/// decision, like the user administration beside it. Nothing here edits a context; its file does.
/// </summary>
public static class AiContextEndpoints
{
    /// <summary>The name the export of earlier content is given.</summary>
    public const string EarlierContentName = "earlier-gui-content";

    public static RouteGroupBuilder MapAiContextEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var contexts = group.MapGroup("/powerai/ai-contexts").WithTags("PowerAI");
        contexts.MapGet(string.Empty, ListAsync).WithName("ListAiContexts");
        contexts.MapGet("/earlier-content", ExportEarlierContentAsync).WithName("ExportEarlierAiContent");
        contexts.MapGet("/{name}", GetAsync).WithName("GetAiContext");
        contexts.MapGet("/{name}/yaml", GetYamlAsync).WithName("GetAiContextYaml");
        contexts.MapPost("/{name}/approve", ApproveAsync).WithName("ApproveAiContext");
        contexts.MapPost("/{name}/revoke", RevokeAsync).WithName("RevokeAiContext");
        return group;
    }

    private static async Task<Ok<AiContextListDto>> ListAsync(CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.AiContexts.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct).ConfigureAwait(false);
        var summaries = await SummarizeAsync(db, rows, ct).ConfigureAwait(false);
        var earlier = await HasEarlierContentAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok(new AiContextListDto(summaries, earlier));
    }

    private static async Task<Results<Ok<AiContextDetailDto>, ProblemHttpResult>> GetAsync(
        string name, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.AiContexts.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(name);
        }

        return TypedResults.Ok(await DetailAsync(db, row, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<AiContextYamlDto>, ProblemHttpResult>> GetYamlAsync(
        string name, CatalogDbContext db, CancellationToken ct)
    {
        var file = await db.AiContexts.AsNoTracking()
            .Where(c => c.Name == name)
            .Select(c => new AiContextYamlDto(c.Path, c.CurrentYaml, c.ApprovedYaml))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return file is null ? NotFound(name) : TypedResults.Ok(file);
    }

    private static async Task<Results<Ok<AiContextDetailDto>, ProblemHttpResult>> ApproveAsync(
        string name, ApproveAiContextRequest request, CatalogDbContext db, ClaimsPrincipal user, TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await db.AiContexts.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(name);
        }

        if (row.CurrentJson is null || row.CurrentHash is null)
        {
            return Refused(
                StatusCodes.Status409Conflict, "Not approvable",
                $"AI context '{name}' has no version to approve: its file does not read. Fix the problems it lists, then sync.");
        }

        if (string.IsNullOrWhiteSpace(request.Version)
            || !string.Equals(request.Version.Trim(), row.CurrentHash, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(
                StatusCodes.Status409Conflict, "Version changed",
                $"AI context '{name}' now proposes version {Short(row.CurrentHash)}, not the one reviewed. Review it again, then approve.");
        }

        // Checked again against the catalog as it stands now: another context may have been approved meanwhile.
        var version = AiContextVersion.FromJson(row.CurrentJson);
        var problems = await AiContextSync.CheckAsync(db, version, row.Path, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            await db.AiContexts.Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Problems, AiContextSync.ProblemsJson(problems)), ct)
                .ConfigureAwait(false);
            return Refused(
                StatusCodes.Status409Conflict, "Not approvable",
                $"AI context '{name}' cannot be approved: {string.Join(" ", problems.Take(5))}");
        }

        var actor = SemanticLayer.Actor(user);
        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var tracked = await db.AiContexts.AsTracking().FirstAsync(c => c.Id == row.Id, ct).ConfigureAwait(false);
                if (!string.Equals(tracked.CurrentHash, row.CurrentHash, StringComparison.Ordinal))
                {
                    throw new SqlFlowException(
                        $"AI context '{name}' changed while it was being approved. Review it again, then approve.");
                }

                tracked.ApprovedJson = tracked.CurrentJson;
                tracked.ApprovedHash = tracked.CurrentHash;
                tracked.ApprovedYaml = tracked.CurrentYaml;
                tracked.ApprovedBy = actor;
                tracked.ApprovedUtc = now;
                tracked.Problems = null;
                tracked.Instructions = version.Instructions;
                await AiContextMaterializer.ApplyAsync(db, tracked.Name, tracked.RepoId, version, actor, now, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return Refused(StatusCodes.Status409Conflict, "Not approvable", ex.Message);
        }

        var approved = await db.AiContexts.AsNoTracking().FirstAsync(c => c.Id == row.Id, ct).ConfigureAwait(false);
        return TypedResults.Ok(await DetailAsync(db, approved, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<AiContextDetailDto>, ProblemHttpResult>> RevokeAsync(
        string name, CatalogDbContext db, CancellationToken ct)
    {
        var exists = await db.AiContexts.AsNoTracking().AnyAsync(c => c.Name == name, ct).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound(name);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var tracked = await db.AiContexts.AsTracking().FirstAsync(c => c.Name == name, ct).ConfigureAwait(false);
            await AiContextMaterializer.RemoveAsync(db, name, ct).ConfigureAwait(false);
            tracked.ApprovedJson = null;
            tracked.ApprovedHash = null;
            tracked.ApprovedYaml = null;
            tracked.ApprovedBy = null;
            tracked.ApprovedUtc = null;
            tracked.Instructions = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var revoked = await db.AiContexts.AsNoTracking().FirstAsync(c => c.Name == name, ct).ConfigureAwait(false);
        return TypedResults.Ok(await DetailAsync(db, revoked, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// What was typed in the GUI before AI contexts were files, as one context file to review and commit: the general
    /// instructions, and every table that has a description, synonyms, a key, column notes, measures or joins of its
    /// own. None of it is served any more; committing the file and approving it brings it back. 404 when there is none.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExportEarlierContentAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var document = await EarlierContentAsync(db, ct).ConfigureAwait(false);
        if (document is null)
        {
            return TypedResults.Problem(
                detail: "Nothing was written in the GUI before AI contexts were files.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        return TypedResults.Text(AiContextYamlWriter.Write(document), "application/yaml; charset=utf-8");
    }

    /// <summary>The earlier content as a context document, or null when there is none.</summary>
    internal static async Task<AiContextDocument?> EarlierContentAsync(CatalogDbContext db, CancellationToken ct)
    {
        var instructions = await db.SemanticLayerSettings.AsNoTracking()
            .Where(s => s.Id == CatalogSemanticLayerSettings.SingletonId)
            .Select(s => s.Instructions)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var annotations = await db.SemanticObjects.AsNoTracking().Where(o => o.AiContext == null)
            .ToListAsync(ct).ConfigureAwait(false);
        // Every column a row is left for was opened or described by an admin in the GUI (the upgrade dropped the rest).
        var columns = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.AiContext == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var measures = await db.SemanticMeasures.AsNoTracking().Where(m => m.AiContext == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var joins = await db.SemanticRelationships.AsNoTracking().Where(r => r.AiContext == null)
            .ToListAsync(ct).ConfigureAwait(false);

        // A table was in the layer when an admin had opened at least one of its columns; only those are carried over,
        // each opening what it opened before: every other column of the table is denied.
        var keys = columns.Select(c => c.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0 && string.IsNullOrWhiteSpace(instructions))
        {
            return null;
        }

        var targetKeys = keys.Concat(joins.Select(j => j.ToObjectKey)).Distinct(StringComparer.Ordinal).ToList();
        var objects = await db.Objects.AsNoTracking()
            .Where(o => targetKeys.Contains(o.Key))
            .Select(o => new { o.Key, o.ServerRef, o.Database, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var catalogColumns = (await db.ObjectColumns.AsNoTracking()
                .Where(c => keys.Contains(c.ObjectKey))
                .OrderBy(c => c.ObjectKey).ThenBy(c => c.Ordinal)
                .Select(c => new { c.ObjectKey, c.Name })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(c => c.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).ToList(), StringComparer.Ordinal);
        var byKey = objects.Where(o => o.Schema is not null).ToDictionary(o => o.Key, StringComparer.Ordinal);
        AiContextTableName? NameOf(string key)
            => byKey.TryGetValue(key, out var o)
                ? new AiContextTableName(o.Database, o.Schema!, o.Name, ComputeTaskPayload.IsWholeReference(o.ServerRef) ? o.ServerRef : null)
                : null;

        var tables = new List<AiContextTable>();
        foreach (var key in keys)
        {
            if (NameOf(key) is not { } name || !catalogColumns.TryGetValue(key, out var all))
            {
                continue;
            }

            var open = columns.Where(c => c.ObjectKey == key).Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deny = all.Where(c => !open.Contains(c)).ToList();
            var described = columns
                .Where(c => c.ObjectKey == key && (c.Description is not null || c.Synonyms is not null))
                .Select(c => new AiContextColumn(c.ColumnName, c.Description, SemanticLayer.SplitSynonyms(c.Synonyms)))
                .ToList();

            var annotation = annotations.FirstOrDefault(a => a.ObjectKey == key);
            var keyColumns = annotation?.KeyColumns is { Length: > 0 } stored ? LineageEndpoints.SplitColumns(stored) : [];
            if (keyColumns.Any(k => !open.Contains(k)))
            {
                keyColumns = [];
            }

            var tableJoins = joins
                .Where(j => j.FromObjectKey == key && LineageEndpoints.SplitColumns(j.FromColumns).All(open.Contains))
                .Select(j => NameOf(j.ToObjectKey) is { } to
                    ? new AiContextJoin(
                        to, LineageEndpoints.SplitColumns(j.FromColumns), LineageEndpoints.SplitColumns(j.ToColumns),
                        string.Equals(j.JoinType, SemanticJoinType.Left, StringComparison.Ordinal) ? AiContextJoinTypes.Left : AiContextJoinTypes.Inner,
                        j.Description)
                    : null)
                .OfType<AiContextJoin>()
                .ToList();

            tables.Add(new AiContextTable(
                name, annotation?.BusinessName, annotation?.Description, SemanticLayer.SplitSynonyms(annotation?.Synonyms),
                keyColumns, described, deny,
                measures.Where(m => m.ObjectKey == key).Select(m => new AiContextMeasure(m.Name, m.Expression, m.Description)).ToList(),
                tableJoins, 0));
        }

        return new AiContextDocument(
            EarlierContentName,
            "What was written in the GUI before AI contexts were files. Review it (each table denies the columns that were closed), then commit and approve.",
            string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim(),
            tables,
            []);
    }

    private static async Task<bool> HasEarlierContentAsync(CatalogDbContext db, CancellationToken ct)
        => await db.SemanticObjects.AsNoTracking().AnyAsync(o => o.AiContext == null, ct).ConfigureAwait(false)
            || await db.ColumnPolicies.AsNoTracking().AnyAsync(p => p.AiContext == null, ct).ConfigureAwait(false)
            || await db.SemanticMeasures.AsNoTracking().AnyAsync(m => m.AiContext == null, ct).ConfigureAwait(false)
            || await db.SemanticRelationships.AsNoTracking().AnyAsync(r => r.AiContext == null, ct).ConfigureAwait(false)
            || await db.SemanticLayerSettings.AsNoTracking().AnyAsync(s => s.Instructions != null && s.Instructions != string.Empty, ct).ConfigureAwait(false);

    private static async Task<AiContextDetailDto> DetailAsync(CatalogDbContext db, CatalogAiContext row, CancellationToken ct)
    {
        var summary = (await SummarizeAsync(db, [row], ct).ConfigureAwait(false))[0];
        var current = row.CurrentJson is null ? null : AiContextVersion.FromJson(row.CurrentJson);
        var approved = row.ApprovedJson is null ? null : AiContextVersion.FromJson(row.ApprovedJson);
        return new AiContextDetailDto(summary, current, approved, Changes(approved, current), AiContextSync.StoredProblems(row));
    }

    private static async Task<IReadOnlyList<AiContextSummaryDto>> SummarizeAsync(
        CatalogDbContext db, IReadOnlyList<CatalogAiContext> rows, CancellationToken ct)
    {
        var names = rows.Select(r => r.Name).ToList();
        var served = await db.SemanticObjects.AsNoTracking()
            .Where(o => o.AiContext != null && names.Contains(o.AiContext))
            .GroupBy(o => o.AiContext!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Name, g => g.Count, ct).ConfigureAwait(false);
        var repoIds = rows.Select(r => r.RepoId).Distinct().ToList();
        var repos = await db.Repos.AsNoTracking().Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        return rows.Select(row =>
        {
            var problems = AiContextSync.StoredProblems(row);
            var tableCount = row.CurrentJson is null ? 0 : AiContextVersion.FromJson(row.CurrentJson).Tables.Count;
            var status = problems.Count > 0 || row.CurrentHash is null ? AiContextStatus.Invalid
                : row.ApprovedHash is null ? AiContextStatus.AwaitingApproval
                : string.Equals(row.ApprovedHash, row.CurrentHash, StringComparison.Ordinal) ? AiContextStatus.Approved
                : AiContextStatus.Changed;
            return new AiContextSummaryDto(
                row.Name, row.Description, row.RepoId, repos.TryGetValue(row.RepoId, out var repo) ? repo : row.RepoId.ToString(),
                row.Path, status, row.CurrentHash, row.ApprovedHash, row.ApprovedBy, row.ApprovedUtc, row.SyncedUtc,
                tableCount, served.TryGetValue(row.Name, out var count) ? count : 0, problems.Count);
        }).ToList();
    }

    /// <summary>What approving <paramref name="current"/> in place of <paramref name="approved"/> would change, in the
    /// order a reviewer reads a context: the context itself, then table by table, then the examples.</summary>
    internal static IReadOnlyList<AiContextChangeDto> Changes(AiContextVersion? approved, AiContextVersion? current)
    {
        var changes = new List<AiContextChangeDto>();
        if (current is null)
        {
            return changes;
        }

        if (approved is null)
        {
            changes.AddRange(current.Tables.Select(t => new AiContextChangeDto(
                "added", $"table {t.Display}", $"{t.Columns.Count} column(s), {t.Measures.Count} measure(s), {t.Joins.Count} join(s)")));
            if (current.Examples.Count > 0)
            {
                changes.Add(new AiContextChangeDto("added", "example questions", $"{current.Examples.Count} question(s)"));
            }

            if (current.Instructions is not null)
            {
                changes.Add(new AiContextChangeDto("added", "the context's instructions", null));
            }

            return changes;
        }

        if (!string.Equals(approved.Description, current.Description, StringComparison.Ordinal))
        {
            changes.Add(new AiContextChangeDto("changed", "the context's description", null));
        }

        Compare(changes, "the context's instructions", approved.Instructions, current.Instructions);

        var before = approved.Tables.ToDictionary(t => t.ObjectKey, StringComparer.Ordinal);
        var after = current.Tables.ToDictionary(t => t.ObjectKey, StringComparer.Ordinal);
        foreach (var table in current.Tables.Where(t => !before.ContainsKey(t.ObjectKey)))
        {
            changes.Add(new AiContextChangeDto("added", $"table {table.Display}", $"{table.Columns.Count} column(s)"));
        }

        foreach (var table in approved.Tables.Where(t => !after.ContainsKey(t.ObjectKey)))
        {
            changes.Add(new AiContextChangeDto("removed", $"table {table.Display}", null));
        }

        foreach (var table in current.Tables.Where(t => before.ContainsKey(t.ObjectKey)))
        {
            var old = before[table.ObjectKey];
            var subject = $"table {table.Display}";
            Compare(changes, $"business name of {subject}", old.BusinessName, table.BusinessName);
            Compare(changes, $"description of {subject}", old.Description, table.Description);
            Compare(changes, $"synonyms of {subject}", string.Join(", ", old.Synonyms), string.Join(", ", table.Synonyms));
            Compare(changes, $"key of {subject}", string.Join(", ", old.Key), string.Join(", ", table.Key));

            var oldColumns = old.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var newColumns = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns.Where(c => !oldColumns.ContainsKey(c.Name)))
            {
                changes.Add(new AiContextChangeDto("added", $"column {column.Name} of {subject}", null));
            }

            foreach (var column in old.Columns.Where(c => !newColumns.ContainsKey(c.Name)))
            {
                changes.Add(new AiContextChangeDto("removed", $"column {column.Name} of {subject}", null));
            }

            foreach (var column in table.Columns.Where(c => oldColumns.ContainsKey(c.Name)))
            {
                var was = oldColumns[column.Name];
                if (!string.Equals(was.Description, column.Description, StringComparison.Ordinal)
                    || !was.Synonyms.SequenceEqual(column.Synonyms, StringComparer.Ordinal))
                {
                    changes.Add(new AiContextChangeDto("changed", $"column {column.Name} of {subject}", "description or synonyms"));
                }
            }

            var oldMeasures = old.Measures.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            var newMeasures = table.Measures.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var measure in table.Measures)
            {
                if (!oldMeasures.TryGetValue(measure.Name, out var was))
                {
                    changes.Add(new AiContextChangeDto("added", $"measure {measure.Name} of {subject}", measure.Expression));
                }
                else if (!string.Equals(was.Expression, measure.Expression, StringComparison.Ordinal)
                    || !string.Equals(was.Description, measure.Description, StringComparison.Ordinal))
                {
                    changes.Add(new AiContextChangeDto("changed", $"measure {measure.Name} of {subject}", measure.Expression));
                }
            }

            foreach (var measure in old.Measures.Where(m => !newMeasures.ContainsKey(m.Name)))
            {
                changes.Add(new AiContextChangeDto("removed", $"measure {measure.Name} of {subject}", null));
            }

            var oldJoins = old.Joins.ToDictionary(j => j.Identity, StringComparer.OrdinalIgnoreCase);
            var newJoins = table.Joins.ToDictionary(j => j.Identity, StringComparer.OrdinalIgnoreCase);
            foreach (var join in table.Joins)
            {
                if (!oldJoins.TryGetValue(join.Identity, out var was))
                {
                    changes.Add(new AiContextChangeDto("added", $"join from {subject} to {join.ToDisplay}", string.Join(", ", join.Columns)));
                }
                else if (!string.Equals(was.JoinType, join.JoinType, StringComparison.Ordinal)
                    || !string.Equals(was.Description, join.Description, StringComparison.Ordinal))
                {
                    changes.Add(new AiContextChangeDto("changed", $"join from {subject} to {join.ToDisplay}", null));
                }
            }

            foreach (var join in old.Joins.Where(j => !newJoins.ContainsKey(j.Identity)))
            {
                changes.Add(new AiContextChangeDto("removed", $"join from {subject} to {join.ToDisplay}", null));
            }
        }

        var oldExamples = approved.Examples.ToDictionary(e => e.Question, StringComparer.OrdinalIgnoreCase);
        var newExamples = current.Examples.ToDictionary(e => e.Question, StringComparer.OrdinalIgnoreCase);
        foreach (var example in current.Examples)
        {
            if (!oldExamples.TryGetValue(example.Question, out var was))
            {
                changes.Add(new AiContextChangeDto("added", $"example '{example.Question}'", null));
            }
            else if (!string.Equals(was.Sql, example.Sql, StringComparison.Ordinal)
                || !string.Equals(was.SourceRef, example.SourceRef, StringComparison.Ordinal))
            {
                changes.Add(new AiContextChangeDto("changed", $"example '{example.Question}'", "its SQL or datasource"));
            }
        }

        foreach (var example in approved.Examples.Where(e => !newExamples.ContainsKey(e.Question)))
        {
            changes.Add(new AiContextChangeDto("removed", $"example '{example.Question}'", null));
        }

        return changes;
    }

    private static void Compare(List<AiContextChangeDto> changes, string subject, string? before, string? after)
    {
        if (string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new AiContextChangeDto(
            string.IsNullOrEmpty(before) ? "added" : string.IsNullOrEmpty(after) ? "removed" : "changed", subject, null));
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static ProblemHttpResult NotFound(string name)
        => TypedResults.Problem(detail: $"No AI context is named '{name}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Refused(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
