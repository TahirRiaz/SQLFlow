using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.AiContext;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// What a repository sync does with the AI context files it finds. It reads each file, resolves every table, column and
/// join target against the catalog, and checks every measure and example with the same guards an assistant's queries
/// pass, as if the version were approved. It records the version a file proposes, with any problems, beside the version
/// an admin approved. An approved context then serves only what its file still gives: a dropped table, column, measure,
/// join or example stops being served at once, while anything added or changed waits for the next approval. A context
/// whose file is gone is removed, and stops being served.
/// <para>
/// Nothing here approves anything, so nothing a file adds ever reaches an assistant without an admin. A context name or
/// a table that another repository's file already holds is reported and left alone.
/// </para>
/// </summary>
public static class AiContextSync
{
    /// <summary>The largest AI context file read. A context is a curated description, not a dump of the catalog, so a
    /// file past this is refused rather than read into memory.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>The most problems listed for one context; the rest are counted.</summary>
    private const int MaxProblems = 50;

    /// <summary>The most column names a "not a column" message lists.</summary>
    private const int MaxListedColumns = 20;

    private static readonly JsonSerializerOptions ProblemJson = new() { WriteIndented = false };

    /// <summary>
    /// Reads the AI context files under <paramref name="repoRoot"/> for repository <paramref name="repoId"/>, records the
    /// version each proposes, narrows what approved contexts serve, and removes the contexts this repository no longer
    /// defines. Returns what a sync's trace should say.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        CatalogDbContext db, Guid repoId, string repoRoot, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(repoRoot);
        var warnings = new List<string>();
        var files = ReadFiles(repoRoot, warnings);

        var owners = await db.AiContexts.AsNoTracking()
            .Select(c => new { c.Name, c.RepoId, c.Path, c.CurrentHash, HasProblems = c.Problems != null })
            .ToListAsync(ct).ConfigureAwait(false);
        var ownerByName = owners.ToDictionary(o => o.Name, StringComparer.Ordinal);
        var repoNames = await db.Repos.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        // Which file of this repository defines which context: the first in path order, unless another repository's
        // file already does.
        var claimed = new Dictionary<string, (string Path, AiContextParse Parse, string Text)>(StringComparer.Ordinal);
        foreach (var (path, parse, text) in files)
        {
            if (parse.Name is null)
            {
                warnings.AddRange(parse.Problems);
                continue;
            }

            if (claimed.TryGetValue(parse.Name, out var first))
            {
                warnings.Add($"{path}: AI context '{parse.Name}' is already defined by {first.Path}; this file is ignored.");
                continue;
            }

            if (ownerByName.TryGetValue(parse.Name, out var owner) && owner.RepoId != repoId)
            {
                var repo = repoNames.TryGetValue(owner.RepoId, out var repoName) ? repoName : owner.RepoId.ToString();
                warnings.Add(
                    $"{path}: AI context '{parse.Name}' is defined by repository '{repo}' ({owner.Path}); this file is ignored "
                    + "while that one exists. Give one of them another name.");
                continue;
            }

            claimed[parse.Name] = (path, parse, text);
        }

        // Resolve and check every claimed context before anything is written, so the writes below are one short step.
        var proposals = new List<Proposal>();
        foreach (var (name, (path, parse, text)) in claimed)
        {
            var problems = new List<string>(parse.Problems);
            AiContextVersion? version = null;
            if (parse.Document is { } document)
            {
                (version, var resolveProblems) = await ResolveAsync(db, document, path, ct).ConfigureAwait(false);
                problems.AddRange(resolveProblems);

                // A repository syncs on its interval whether or not anything changed, so a version this sync resolves
                // exactly as the last one did, which had no problems then, is not checked again: approving re-checks
                // it against the catalog as it stands, and serving grades every piece as it is read. A version that
                // had problems is checked again, so they clear once their cause is gone.
                var unchanged = version is not null
                    && ownerByName.TryGetValue(name, out var stored) && stored.RepoId == repoId && !stored.HasProblems
                    && string.Equals(stored.CurrentHash, AiContextVersion.HashOf(version.ToJson()), StringComparison.Ordinal);
                if (version is not null && !unchanged)
                {
                    problems.AddRange(await CheckAsync(db, version, path, ct).ConfigureAwait(false));
                }
            }

            if (problems.Count > 0)
            {
                warnings.Add(
                    $"{path}: AI context '{name}' cannot be approved until its {Count(problems.Count, "problem")} "
                    + $"{(problems.Count == 1 ? "is" : "are")} fixed:");
                warnings.AddRange(problems.Take(MaxProblems).Select(p => "  " + p));
            }

            proposals.Add(new Proposal(name, path, parse.Document?.Description, version, problems, text));
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var rows = await db.AiContexts.AsTracking()
                .Where(c => c.RepoId == repoId)
                .ToListAsync(ct).ConfigureAwait(false);
            var rowByName = rows.ToDictionary(r => r.Name, StringComparer.Ordinal);

            foreach (var row in rows.Where(r => !claimed.ContainsKey(r.Name)))
            {
                // The file is gone, so the context is: nothing it served stays served.
                await AiContextMaterializer.RemoveAsync(db, row.Name, ct).ConfigureAwait(false);
                db.AiContexts.Remove(row);
            }

            foreach (var proposal in proposals)
            {
                if (!rowByName.TryGetValue(proposal.Name, out var row))
                {
                    row = new CatalogAiContext { Name = proposal.Name, RepoId = repoId };
                    db.AiContexts.Add(row);
                }

                row.Path = proposal.Path;
                row.Description = proposal.Description;
                row.CurrentJson = proposal.Version?.ToJson();
                row.CurrentHash = row.CurrentJson is null ? null : AiContextVersion.HashOf(row.CurrentJson);
                row.Problems = proposal.Problems.Count == 0 ? null : ProblemsJson(proposal.Problems);
                row.CurrentYaml = proposal.Text;
                row.SyncedUtc = nowUtc;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Narrow what each approved context serves to what its file still gives, one context at a time so a context
        // that cannot be served as it was does not hold the others back. A file that does not read narrows nothing: its
        // approved version is served as it was until the file is fixed.
        var approved = (await db.AiContexts.AsNoTracking()
                .Where(c => c.RepoId == repoId && c.ApprovedHash != null)
                .Select(c => c.Name)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var proposal in proposals.Where(p => p.Version is not null && approved.Contains(p.Name)))
        {
            try
            {
                await ServeEffectiveAsync(db, proposal.Name, nowUtc, ct).ConfigureAwait(false);
            }
            catch (SqlFlowException ex)
            {
                warnings.Add($"{proposal.Path}: AI context '{proposal.Name}' is served as it was: {ex.Message}");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Serves context <paramref name="name"/>'s effective version: its approved version narrowed by the version its file
    /// currently proposes, or as approved while the file does not read. Serves nothing while nothing is approved. Writes
    /// in a transaction of its own, so a refusal leaves the catalog as it was.
    /// </summary>
    public static async Task ServeEffectiveAsync(CatalogDbContext db, string name, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = await db.AiContexts.AsTracking().FirstOrDefaultAsync(c => c.Name == name, ct).ConfigureAwait(false);
            if (row is null)
            {
                await AiContextMaterializer.RemoveAsync(db, name, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return;
            }

            var effective = Effective(row);
            await AiContextMaterializer.ApplyAsync(db, row.Name, row.RepoId, effective, row.ApprovedBy, nowUtc, ct).ConfigureAwait(false);
            row.Instructions = effective?.Instructions;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>What <paramref name="row"/> serves: its approved version narrowed by its current one, the approved
    /// version as it is while the file does not read, or null while none is approved.</summary>
    public static AiContextVersion? Effective(CatalogAiContext row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ApprovedJson is null)
        {
            return null;
        }

        var approved = AiContextVersion.FromJson(row.ApprovedJson);
        return row.CurrentJson is null ? approved : approved.NarrowedTo(AiContextVersion.FromJson(row.CurrentJson));
    }

    /// <summary>The problems of <paramref name="row"/>'s current version, as stored.</summary>
    public static IReadOnlyList<string> StoredProblems(CatalogAiContext row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Problems is null ? [] : JsonSerializer.Deserialize<List<string>>(row.Problems) ?? [];
    }

    /// <summary>The stored form of <paramref name="problems"/>, at most <see cref="MaxProblems"/> of them.</summary>
    public static string ProblemsJson(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var kept = problems.Take(MaxProblems).ToList();
        if (problems.Count > MaxProblems)
        {
            kept.Add($"... and {problems.Count - MaxProblems} more.");
        }

        return JsonSerializer.Serialize(kept, ProblemJson);
    }

    /// <summary>
    /// Why <paramref name="version"/> cannot be served as it stands, checked as if it were approved: a table, a measure
    /// name or a join another context serves, and every measure and example through the read-only and
    /// column allow-list guards with this version's columns as the allowed ones on its tables. Empty when it can.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(
        CatalogDbContext db, AiContextVersion version, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(version);
        var problems = new List<string>();
        var keys = version.Tables.Select(t => t.ObjectKey).ToList();

        var takenTables = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.AiContext != null && p.AiContext != version.Name && keys.Contains(p.ObjectKey))
            .Select(p => new { p.ObjectKey, p.AiContext })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var taken in takenTables)
        {
            var display = version.Tables.First(t => t.ObjectKey == taken.ObjectKey).Display;
            problems.Add($"{path}: table '{display}' is already in AI context '{taken.AiContext}'; a table belongs to one context.");
        }

        var measureNames = version.Tables.SelectMany(t => t.Measures.Select(m => m.Name)).ToList();
        var takenMeasures = await db.SemanticMeasures.AsNoTracking()
            .Where(m => m.AiContext != null && m.AiContext != version.Name && measureNames.Contains(m.Name))
            .Select(m => new { m.Name, m.AiContext })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var taken in takenMeasures)
        {
            problems.Add($"{path}: measure '{taken.Name}' is already served by AI context '{taken.AiContext}'; a measure name is unique.");
        }

        // A join is the same join from either end, so both directions are looked for.
        var joins = version.Tables
            .SelectMany(t => t.Joins.Select(j => (
                Subject: $"the join from '{t.Display}' to '{j.ToDisplay}'",
                Forward: SemanticRelationshipIdentity.Compute(t.ObjectKey, j.Columns, j.ToObjectKey, j.ToColumns),
                Backward: SemanticRelationshipIdentity.Compute(j.ToObjectKey, j.ToColumns, t.ObjectKey, j.Columns))))
            .ToList();
        var identities = joins.SelectMany(j => new[] { j.Forward, j.Backward }).Distinct(StringComparer.Ordinal).ToList();
        var takenJoins = await db.SemanticRelationships.AsNoTracking()
            .Where(r => r.AiContext != null && r.AiContext != version.Name && identities.Contains(r.IdentityHash))
            .Select(r => new { r.IdentityHash, r.AiContext })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var taken in takenJoins)
        {
            var join = joins.First(j => j.Forward == taken.IdentityHash || j.Backward == taken.IdentityHash);
            problems.Add($"{path}: {join.Subject} is already served by AI context '{taken.AiContext}'; declare it in one of the two files.");
        }

        // The allow-list as it would stand with this version approved: its tables open exactly the columns it lists, and
        // a table it served before but no longer lists opens nothing.
        var allowed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var previous = await db.ColumnPolicies.AsNoTracking()
            .Where(p => p.AiContext == version.Name)
            .Select(p => p.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var key in previous)
        {
            allowed[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var table in version.Tables)
        {
            allowed[table.ObjectKey] = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var table in version.Tables)
        {
            foreach (var measure in table.Measures)
            {
                if (await SemanticLayer.MeasureProblemAsync(db, table.ObjectKey, measure.Expression, ct, allowed).ConfigureAwait(false)
                    is { } problem)
                {
                    problems.Add($"{path}: measure '{measure.Name}' of table '{table.Display}': {problem}");
                }
            }
        }

        foreach (var example in version.Examples)
        {
            if (await SemanticLayer.QueryProblemAsync(db, example.Sql, ct, allowed).ConfigureAwait(false) is { } problem)
            {
                problems.Add($"{path}: the example '{example.Question}': {problem}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Resolves <paramref name="document"/> against the catalog: each table and join target to the one catalogued object
    /// it names, each column to the catalog's spelling of it, and each example to the objects its SQL reads and the
    /// datasource it runs on. The version is null when anything does not resolve; the problems say what.
    /// </summary>
    public static async Task<(AiContextVersion? Version, IReadOnlyList<string> Problems)> ResolveAsync(
        CatalogDbContext db, AiContextDocument document, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();

        var names = document.Tables.Select(t => t.Table.Name)
            .Concat(document.Tables.SelectMany(t => t.Joins.Select(j => j.To.Name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = await db.Objects.AsNoTracking()
            .Where(o => names.Contains(o.Name))
            .Select(o => new CatalogCandidate(o.Key, o.ServerRef, o.Database, o.Schema, o.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        var candidateKeys = candidates.Select(c => c.Key).ToList();
        var columnsByKey = (await db.ObjectColumns.AsNoTracking()
                .Where(c => candidateKeys.Contains(c.ObjectKey))
                .OrderBy(c => c.ObjectKey).ThenBy(c => c.Ordinal)
                .Select(c => new { c.ObjectKey, c.Name })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(c => c.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => c.Name).ToList(),
                StringComparer.Ordinal);

        var tables = new List<AiContextVersionTable>();
        foreach (var table in document.Tables)
        {
            var where = $"{path}:{table.Line}";
            if (Match(table.Table, candidates, columnsByKey, where, problems) is not { } match)
            {
                continue;
            }

            var catalogColumns = columnsByKey[match.Key];
            var spelling = catalogColumns.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            string? Column(string name, string what)
            {
                if (spelling.TryGetValue(name, out var canonical))
                {
                    return canonical;
                }

                problems.Add(
                    $"{where}: {what} '{name}' is not a column of '{table.Table.Display}'; it has {List(catalogColumns)}.");
                return null;
            }

            // Every column of the table is open but the denied ones, each with what the file says it means. A name the file
            // describes or denies must be one of the table's columns, or the file says something about nothing.
            var denied = table.Deny.Select(d => Column(d, "denied column")).OfType<string>().ToList();
            var described = new Dictionary<string, AiContextColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (Column(column.Name, "described column") is { } name)
                {
                    described[name] = column;
                }
            }

            var deniedSet = denied.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = catalogColumns
                .Where(c => !deniedSet.Contains(c))
                .Select(c => described.TryGetValue(c, out var note)
                    ? new AiContextVersionColumn(c, note.Description, note.Synonyms)
                    : new AiContextVersionColumn(c, null, []))
                .ToList();
            if (columns.Count == 0)
            {
                problems.Add($"{where}: '{table.Table.Display}' denies every column it has, so it opens nothing; leave it out instead.");
            }

            var key = table.Key.Select(k => Column(k, "key column")).OfType<string>().ToList();

            var joins = new List<AiContextVersionJoin>();
            foreach (var join in table.Joins)
            {
                if (Match(join.To, candidates, columnsByKey, $"{where}: the join to '{join.To.Display}'", problems) is not { } target)
                {
                    continue;
                }

                var targetSpelling = columnsByKey[target.Key].ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
                var toColumns = new List<string>();
                foreach (var column in join.ToColumns)
                {
                    if (targetSpelling.TryGetValue(column, out var canonical))
                    {
                        toColumns.Add(canonical);
                    }
                    else
                    {
                        problems.Add(
                            $"{where}: the join to '{join.To.Display}' pairs '{column}', which is not one of its columns; it has "
                            + $"{List(columnsByKey[target.Key])}.");
                    }
                }

                var fromColumns = join.Columns.Select(c => Column(c, "join column")).OfType<string>().ToList();
                if (fromColumns.Count == join.Columns.Count && toColumns.Count == join.ToColumns.Count)
                {
                    joins.Add(new AiContextVersionJoin(
                        target.Key, join.To.Display, fromColumns, toColumns,
                        join.JoinType == AiContextJoinTypes.Left ? SemanticJoinType.Left : SemanticJoinType.Inner,
                        join.Description));
                }
            }

            tables.Add(new AiContextVersionTable(
                match.Key, table.Table.Display, table.BusinessName, table.Description, table.Synonyms, key, columns, denied,
                table.Measures.Select(m => new AiContextVersionMeasure(m.Name, m.Expression, m.Description)).ToList(),
                joins));
        }

        var duplicates = tables.GroupBy(t => t.ObjectKey, StringComparer.Ordinal).Where(g => g.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            problems.Add($"{path}: '{string.Join("' and '", duplicate.Select(t => t.Display))}' name the same table.");
        }

        // A join is one relationship whichever end declares it, so declaring it twice, or once from each end, is once too
        // many: the assistant would be served the same join twice.
        var declaredJoins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            foreach (var join in table.Joins)
            {
                var forward = SemanticRelationshipIdentity.Compute(table.ObjectKey, join.Columns, join.ToObjectKey, join.ToColumns);
                var backward = SemanticRelationshipIdentity.Compute(join.ToObjectKey, join.ToColumns, table.ObjectKey, join.Columns);
                if (declaredJoins.Contains(forward) || declaredJoins.Contains(backward))
                {
                    problems.Add(
                        $"{path}: the join from '{table.Display}' to '{join.ToDisplay}' on {string.Join(", ", join.Columns)} is "
                        + "declared twice (a join is the same from either end); keep one.");
                }

                declaredJoins.Add(forward);
            }
        }

        var examples = new List<AiContextVersionExample>();
        foreach (var example in document.Examples)
        {
            var question = QuestionExampleEndpoints.NormalizeQuestion(example.Question);
            if (question.Length == 0)
            {
                problems.Add($"{path}: an example's question is only the business-question command.");
                continue;
            }

            if (example.Datasource is { } given && !given.StartsWith('@')
                && !await DatasourceInference.IsDeclaredAsync(db, given, ct).ConfigureAwait(false))
            {
                problems.Add($"{path}: the example '{question}' names a datasource no active pipeline declares.");
                continue;
            }

            var objectKeys = await DatasourceInference.ObjectKeysFromSqlAsync(db, example.Sql, ct).ConfigureAwait(false);
            var sourceRef = example.Datasource
                ?? (await DatasourceInference.InferAsync(db, example.Sql, objectKeys, ct).ConfigureAwait(false)).Reference;
            examples.Add(new AiContextVersionExample(question, example.Sql, sourceRef, objectKeys));
        }

        var questionDuplicates = examples.GroupBy(e => e.Question, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var duplicate in questionDuplicates)
        {
            problems.Add($"{path}: the question '{duplicate.Key}' is given twice once the business-question command is removed.");
        }

        return problems.Count > 0
            ? (null, problems)
            : (new AiContextVersion(document.Name, document.Description, document.Instructions, tables, examples), problems);
    }

    private static CatalogCandidate? Match(
        AiContextTableName name, List<CatalogCandidate> candidates, Dictionary<string, List<string>> columnsByKey,
        string where, List<string> problems)
    {
        var matches = candidates
            .Where(c => string.Equals(c.Name, name.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Schema, name.Schema, StringComparison.OrdinalIgnoreCase)
                && (name.Database is null || string.Equals(c.Database, name.Database, StringComparison.OrdinalIgnoreCase))
                && (name.Datasource is null || string.Equals(c.ServerRef, name.Datasource, StringComparison.OrdinalIgnoreCase))
                && columnsByKey.ContainsKey(c.Key))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            problems.Add(
                $"{where}: '{name.Display}'{(name.Datasource is null ? string.Empty : $" on {name.Datasource}")} is not a table or "
                + "view the catalog holds with its columns. A repository sync or a datasource scan must have read it first.");
            return null;
        }

        problems.Add(
            $"{where}: '{name.Display}' names {matches.Count} catalogued objects ("
            + string.Join(", ", matches.Select(m => $"{m.Database}.{m.Schema}.{m.Name} on {m.ServerRef}"))
            + "). Give the database, and 'datasource' with the connection reference it lives on.");
        return null;
    }

    /// <summary>The AI context files under <paramref name="repoRoot"/>, each with its parse and its text, credential-redacted
    /// as a flow's YAML is before it is stored, in path order.</summary>
    private static List<(string Path, AiContextParse Parse, string Text)> ReadFiles(string repoRoot, List<string> warnings)
    {
        var result = new List<(string, AiContextParse, string)>();
        if (!Directory.Exists(repoRoot))
        {
            warnings.Add($"AI contexts were not read: the repository folder '{repoRoot}' does not exist.");
            return result;
        }

        var files = Directory.EnumerateFiles(repoRoot, "*.yaml", SearchOption.AllDirectories)
            .Where(YamlAiContextLoader.IsAiContextFile)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(repoRoot, f).Replace('\\', '/')))
            .Where(f => !f.Relative.StartsWith(".git/", StringComparison.Ordinal))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
        foreach (var (full, relative) in files)
        {
            try
            {
                var length = new FileInfo(full).Length;
                if (length > MaxFileBytes)
                {
                    warnings.Add($"{relative}: skipped: {length} bytes is more than an AI context file may hold ({MaxFileBytes}).");
                    continue;
                }

                var text = File.ReadAllText(full);
                result.Add((relative, YamlAiContextLoader.Parse(text, relative), SecretHygiene.RedactedMessage(text)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relative}: skipped: {ex.Message}");
            }
        }

        return result;
    }

    private static string List(IReadOnlyList<string> columns)
        => columns.Count <= MaxListedColumns
            ? string.Join(", ", columns)
            : $"{string.Join(", ", columns.Take(MaxListedColumns))} and {columns.Count - MaxListedColumns} more";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private sealed record CatalogCandidate(string Key, string ServerRef, string? Database, string? Schema, string Name);

    private sealed record Proposal(
        string Name, string Path, string? Description, AiContextVersion? Version, IReadOnlyList<string> Problems, string Text);
}
