using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A catalog object that matched a name search: the identity a client needs to locate it.</summary>
public sealed record ObjectHitDto(
    string Key, string Name, string Kind, string ServerRef, string? Database, string? Schema);

/// <summary>A column that matched a name search, carried with its owning object for context: this answers
/// "every table/view with a column named X" across all repos.</summary>
public sealed record ColumnHitDto(
    string ObjectKey, string ObjectName, string ColumnName, string? DataType, bool Nullable);

/// <summary>An object whose code matched a definition search, with a short excerpt around the first occurrence so a
/// list can show why it matched. <see cref="Source"/> says which body carried the match: the live module body
/// (<c>Module</c>) or the generating DDL the engine emitted (<c>Script</c>).</summary>
public sealed record DefinitionHitDto(string Key, string Name, string Kind, string Snippet, string Source);

/// <summary>A file a run processed whose name or path matched, carried with its run so the hit deep-links to the
/// run that touched it: this answers "which runs processed sess.20180529.csv". <see cref="PipelineId"/> and
/// <see cref="RepoId"/> are the flow that ingested the file and its repo, so the hit can also jump to that flow's
/// node in the lineage graph.</summary>
public sealed record FileHitDto(
    string Name, string? Path, string RunId, string FlowName, string FlowKind,
    long Rows, int Columns, long SizeBytes, DateTime? RunUtc, string PipelineId, string? RepoId, string? RepoName);

/// <summary>A flow (YAML document) that matched by name, repo-relative path, or a term inside its body, with a short
/// excerpt when the match was in the YAML text. <see cref="MatchedIn"/> is <c>Name</c>, <c>Path</c>, or <c>Body</c>.</summary>
public sealed record FlowHitDto(
    string Id, string Name, string Kind, string? Batch, string RelativePath, string RepoId, string RepoName,
    string MatchedIn, string Snippet);

/// <summary>
/// A column a FLOW produces, matched by its output name, the raw source column it reads, or the SQL expression that
/// computes it. This is the surface that answers "which pipeline computes SourceRank": a column that exists only
/// inside a flow's transformation view is invisible to <see cref="ColumnHitDto"/> until (and unless) the warehouse
/// object it lands in has been synced. <see cref="Kind"/> is <c>declared</c> (authored in the YAML) or
/// <c>detected</c> (inferred by a run from the raw data); <see cref="MatchedIn"/> is <c>Column</c>, <c>Source</c>,
/// or <c>Expression</c>.
/// </summary>
public sealed record FlowColumnHitDto(
    string PipelineId, string FlowName, string FlowKind, string? Batch, string RepoId, string RepoName,
    string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? DataType, string? Expression,
    string MatchedIn);

/// <summary>
/// SQL a flow actually EXECUTED, grouped to one row per (flow, step) because the engine regenerates near-identical
/// statements on every run: <see cref="Occurrences"/> is how many statement rows in the window matched, and the
/// sample is the newest of them. This is the ground truth of what ran, which the authored YAML and the object's
/// stored module body both only approximate: an expression the engine composes (a merge projection, a generated
/// cast) exists nowhere else in the catalog.
/// </summary>
public sealed record StatementHitDto(
    string PipelineId, string FlowName, string FlowKind, string Step,
    long Occurrences, string RunId, DateTime? LastSeenUtc, string Snippet);

/// <summary>One data subscriber matching a search: a report, workbook, notebook, or application that CONSUMES the
/// warehouse. <see cref="Key"/> opens its dossier. <see cref="Notes"/> is carried because it is often the reason
/// the row matched (searching "Incomplete dataset" finds every consumer whose lineage is only partial) and because
/// a stale or superseded report is exactly what a person searching the estate needs to see about it.</summary>
/// <para><see cref="Type"/> is the consuming tool (PowerBI / Tableau / Excel / ...). It is named the same here as
/// on every other subscriber shape, and deliberately NOT "kind": a client that links rows by the identity fields
/// they carry reads `key` + `kind` as a warehouse object, which a subscriber is not.</para>
public sealed record SubscriberHitDto(
    string Key, string Name, string Type, string? Owner, string? Description, string? Notes,
    string? Url, string RepoId, string File);

/// <summary>One category of a combined search: the full match count plus a small preview of the top hits, so the
/// unified view can show "Files (37)" with the first few and a jump to the dedicated tab for the rest.</summary>
public sealed record SearchCategoryDto<T>(long Total, IReadOnlyList<T> Items);

/// <summary>
/// The combined result of a single global search across every catalog surface, each category counted in full and
/// previewed with its top hits. <see cref="Tokens"/> reports how the raw query was actually parsed (a multi-word
/// query is matched token by token, not as a literal phrase), so a caller can see what was searched for and retry
/// with a narrower term when a stray word emptied the result. <see cref="StatementWindowDays"/> is the only bounded
/// surface: executed SQL is high-volume and pruned by retention, so the global view searches a recent window and
/// says which, and the dedicated endpoint can widen it.
/// </summary>
public sealed record AllSearchDto(
    string Query,
    IReadOnlyList<string> Tokens,
    int StatementWindowDays,
    SearchCategoryDto<ObjectHitDto> Objects,
    SearchCategoryDto<ColumnHitDto> Columns,
    SearchCategoryDto<DefinitionHitDto> Definitions,
    SearchCategoryDto<FileHitDto> Files,
    SearchCategoryDto<FlowHitDto> Flows,
    SearchCategoryDto<FlowColumnHitDto> FlowColumns,
    SearchCategoryDto<StatementHitDto> Statements,
    SearchCategoryDto<SubscriberHitDto> Subscribers);

/// <summary>
/// A search term parsed into the raw phrase and the tokens actually matched. A query is matched token by token and
/// every token must appear (AND), which is what lets "ferry passengers" find <c>FerryPassengers_PerDeparture</c>:
/// a single LIKE over the literal phrase can never match a name whose words are run together. Rows where the whole
/// phrase occurs verbatim are ranked first, so the exact-phrase behavior callers expect survives as the top of the
/// list rather than as the whole of it.
/// </summary>
internal sealed record SearchQuery(string Phrase, IReadOnlyList<string> Tokens)
{
    /// <summary>The most tokens one query contributes to the SQL predicate. Each token adds a scan predicate, so
    /// the cap bounds what a pasted sentence can cost; the leading tokens are the ones a caller means.</summary>
    internal const int MaxTokens = 6;

    /// <summary>Single characters are dropped: they match almost everything, and under AND semantics a stray one
    /// would narrow a good query to nothing rather than widen it.</summary>
    private const int MinTokenLength = 2;

    /// <summary>
    /// Parses a raw query, or returns null when it carries nothing searchable (blank, or only one-character
    /// words). Tokens are de-duplicated case-insensitively and capped at <see cref="MaxTokens"/>.
    /// </summary>
    internal static SearchQuery? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var phrase = raw.Trim();
        var tokens = phrase
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= MinTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTokens)
            .ToList();

        return tokens.Count == 0 ? null : new SearchQuery(phrase, tokens);
    }

    /// <summary>How many of the tokens occur in <paramref name="text"/>, used to pick which of several fields
    /// carried a hit.</summary>
    internal int TokenHits(string? text)
        => string.IsNullOrEmpty(text)
            ? 0
            : Tokens.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where to center an excerpt: the whole phrase when it occurs verbatim, otherwise the earliest token
    /// occurrence. Returns (-1, 0) when nothing is found, which happens when the tokens landed in a sibling field
    /// of the same row rather than in this text.
    /// </summary>
    internal (int Index, int Length) FirstMatch(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (-1, 0);
        }

        var phraseAt = text.IndexOf(Phrase, StringComparison.OrdinalIgnoreCase);
        if (phraseAt >= 0)
        {
            return (phraseAt, Phrase.Length);
        }

        var best = (Index: -1, Length: 0);
        foreach (var token in Tokens)
        {
            var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best.Index < 0 || at < best.Index))
            {
                best = (at, token.Length);
            }
        }

        return best;
    }
}

/// <summary>
/// The cross-repo global search over the shadow catalog: one term fanned out across every searchable surface -
/// objects by name, columns by name, code (module bodies and emitted DDL), processed files, flow YAML, and the
/// columns flows produce. Each dedicated endpoint pages one surface; the combined <c>/all</c> endpoint runs the
/// same queries and returns a previewed count per surface for a single unified view.
/// </summary>
/// <remarks>
/// Matching is token-AND with phrase-first ranking (see <see cref="SearchQuery"/>). Tokens may land in different
/// fields of the same row - "orders csv" matches a processed file whose name carries one and whose path carries the
/// other - which is deliberate: a row is a hit when it is about all of the terms, wherever it says so. Every query
/// is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>), projected to a DTO (the raw EF
/// entities never leave the host), and bounded by page size.
/// </remarks>
public static class SearchEndpoints
{
    /// <summary>The longest excerpt a code/flow search returns per hit, in characters.</summary>
    private const int SnippetMaxLength = 200;

    /// <summary>How many hits each category previews in the combined <c>/all</c> view before "see all N" takes over.</summary>
    private const int PreviewSize = 5;

    /// <summary>
    /// How far back an executed-SQL search reaches when the caller does not say. Statement rows are the heaviest
    /// table in the catalog (one row per generated statement per run) and are pruned by the trace retention, so an
    /// unbounded scan is both expensive and misleading about its own coverage. Pass <c>days=0</c> for all history.
    /// </summary>
    private const int DefaultStatementWindowDays = 90;

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var search = group.MapGroup("/search").WithTags("Search");
        search.MapGet("/all", SearchAllAsync).WithName("SearchAll");
        search.MapGet("/objects", SearchObjectsAsync).WithName("SearchObjects");
        search.MapGet("/columns", SearchColumnsAsync).WithName("SearchColumns");
        search.MapGet("/definitions", SearchDefinitionsAsync).WithName("SearchDefinitions");
        search.MapGet("/files", SearchFilesAsync).WithName("SearchFiles");
        search.MapGet("/flows", SearchFlowsAsync).WithName("SearchFlows");
        search.MapGet("/flow-columns", SearchFlowColumnsAsync).WithName("SearchFlowColumns");
        search.MapGet("/statements", SearchStatementsAsync).WithName("SearchStatements");
        search.MapGet("/subscribers", SearchSubscribersAsync).WithName("SearchSubscribers");

        return group;
    }

    private static async Task<Results<Ok<PagedResult<ObjectHitDto>>, ProblemHttpResult>> SearchObjectsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(ObjectsQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<PagedResult<ColumnHitDto>>, ProblemHttpResult>> SearchColumnsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(ColumnsQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Scans object code - both the live module body (<c>Definition</c>) and the generating DDL the engine emitted
    /// (<c>Script</c>) - for a term and returns a bounded excerpt around the first match, tagged with which body
    /// matched. This is a LIKE/Contains scan today; a SQL Server FULLTEXT index over the definition column (already
    /// provisioned by a guarded migration when the feature is installed) is the scalable path for large estates.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DefinitionHitDto>>, ProblemHttpResult>> SearchDefinitionsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = DefinitionRows(db, term);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // The substring-around-the-first-index excerpt does not translate to SQL, so it runs after the page
        // materializes; only the page's rows carry their (large) code columns back.
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => MapDefinition(r, term)).ToList();
        return TypedResults.Ok(new PagedResult<DefinitionHitDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<SubscriberHitDto>>, ProblemHttpResult>> SearchSubscribersAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(SubscribersQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<PagedResult<FileHitDto>>, ProblemHttpResult>> SearchFilesAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(FilesQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<PagedResult<FlowHitDto>>, ProblemHttpResult>> SearchFlowsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = FlowRows(db, term);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // The match-location tag and the YAML excerpt are computed in memory, so only the page's rows carry their
        // (large) YAML column back.
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => MapFlow(r, term)).ToList();
        return TypedResults.Ok(new PagedResult<FlowHitDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<FlowColumnHitDto>>, ProblemHttpResult>> SearchFlowColumnsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = FlowColumnRows(db, term);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => MapFlowColumn(r, term)).ToList();
        return TypedResults.Ok(new PagedResult<FlowColumnHitDto>(items, p, size, total));
    }

    /// <summary>
    /// Searches the SQL the engine actually executed, grouped to one row per (flow, step) so a statement the engine
    /// regenerates on every run reads as one hit with an occurrence count rather than thousands of duplicates. The
    /// sample text is the newest matching statement, fetched for the page's rows in a single follow-up query.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<StatementHitDto>>, ProblemHttpResult>> SearchStatementsAsync(
        CatalogDbContext db, TimeProvider clock, string? q, int? days, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var window = days ?? DefaultStatementWindowDays;
        if (window < 0)
        {
            return BadRequest("'days' must be 0 (all history) or a positive number of days.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = StatementGroups(db, term, StatementCutoff(clock, window));
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var groups = await query
            .Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);

        var items = await MapStatementsAsync(db, groups, term, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<StatementHitDto>(items, p, size, total));
    }

    /// <summary>
    /// One global search across every surface at once: each category is counted in full and previewed with its top
    /// <see cref="PreviewSize"/> hits, so the unified view shows "Files (37)" with the first few and defers the rest
    /// to the dedicated tab. Reuses the exact same query builders the paged endpoints use, so a hit that shows here
    /// is the same hit the tab shows.
    /// </summary>
    private static async Task<Results<Ok<AllSearchDto>, ProblemHttpResult>> SearchAllAsync(
        CatalogDbContext db, TimeProvider clock, string? q, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var objects = await PreviewAsync(ObjectsQuery(db, term), ct).ConfigureAwait(false);
        var columns = await PreviewAsync(ColumnsQuery(db, term), ct).ConfigureAwait(false);
        var files = await PreviewAsync(FilesQuery(db, term), ct).ConfigureAwait(false);
        var subscribers = await PreviewAsync(SubscribersQuery(db, term), ct).ConfigureAwait(false);

        var defQuery = DefinitionRows(db, term);
        var defTotal = await defQuery.LongCountAsync(ct).ConfigureAwait(false);
        var defItems = (await defQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapDefinition(r, term)).ToList();

        var flowQuery = FlowRows(db, term);
        var flowTotal = await flowQuery.LongCountAsync(ct).ConfigureAwait(false);
        var flowItems = (await flowQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapFlow(r, term)).ToList();

        var flowColQuery = FlowColumnRows(db, term);
        var flowColTotal = await flowColQuery.LongCountAsync(ct).ConfigureAwait(false);
        var flowColItems = (await flowColQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapFlowColumn(r, term)).ToList();

        var stmtQuery = StatementGroups(db, term, StatementCutoff(clock, DefaultStatementWindowDays));
        var stmtTotal = await stmtQuery.LongCountAsync(ct).ConfigureAwait(false);
        var stmtGroups = await stmtQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false);
        var stmtItems = await MapStatementsAsync(db, stmtGroups, term, ct).ConfigureAwait(false);

        return TypedResults.Ok(new AllSearchDto(
            term.Phrase,
            term.Tokens,
            DefaultStatementWindowDays,
            objects,
            columns,
            new SearchCategoryDto<DefinitionHitDto>(defTotal, defItems),
            files,
            new SearchCategoryDto<FlowHitDto>(flowTotal, flowItems),
            new SearchCategoryDto<FlowColumnHitDto>(flowColTotal, flowColItems),
            new SearchCategoryDto<StatementHitDto>(stmtTotal, stmtItems),
            subscribers));
    }

    // ---- Shared query builders (one definition per surface, used by both the paged and combined endpoints) --------
    //
    // Each builder ANDs one predicate per token onto its source query, then ranks rows carrying the verbatim phrase
    // first. .Contains over a column with a captured value translates to SQL Server's CHARINDEX, which is
    // wildcard-safe (a term containing % or _ is matched literally) and case-insensitive under the default
    // collation.
    //
    // The token predicates are ALWAYS chained onto the entities (an anonymous join shape where a join is needed),
    // never onto a projected record: EF Core cannot see through a constructor projection, so filtering after one
    // fails to translate at runtime. The record projection is therefore always the last step, after the ordering.
    // SearchTranslationTests renders each query to SQL and would catch a regression here.

    internal static IQueryable<ObjectHitDto> ObjectsQuery(CatalogDbContext db, SearchQuery term)
    {
        var rows = db.Objects.AsNoTracking();
        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(o => o.Name.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(o => o.Name.Contains(phrase))
            .ThenBy(o => o.Name).ThenBy(o => o.Key)
            .Select(o => new ObjectHitDto(o.Key, o.Name, o.Kind, o.ServerRef, o.Database, o.Schema));
    }

    // The consumption side of the estate. A subscriber is not a database object and not a flow, so neither of
    // those surfaces can find it; without its own builder, searching a report by name returns nothing and the
    // consumer looks absent rather than unsearched. Every field a person would search by is matched: the report's
    // name, the tool, the owner, what it is for, the remarks about its state, WHERE IT LIVES, and the file
    // declaring it. Notes are in deliberately, because "Incomplete dataset" is the term that finds every consumer
    // whose lineage is partial; Url is in because a person often knows only where a report lives (a workspace, a
    // share, a folder) and needs to get from that back to what it reads.
    internal static IQueryable<SubscriberHitDto> SubscribersQuery(CatalogDbContext db, SearchQuery term)
    {
        var rows = db.Subscribers.AsNoTracking();
        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(s => s.Name.Contains(t)
                || s.Type.Contains(t)
                || (s.Owner != null && s.Owner.Contains(t))
                || (s.Description != null && s.Description.Contains(t))
                || (s.Notes != null && s.Notes.Contains(t))
                || (s.Url != null && s.Url.Contains(t))
                || s.File.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(s => s.Name.Contains(phrase))
            .ThenBy(s => s.Name).ThenBy(s => s.ObjectKey)
            .Select(s => new SubscriberHitDto(
                s.ObjectKey, s.Name, s.Type, s.Owner, s.Description, s.Notes, s.Url,
                s.RepoId.ToString(), s.File));
    }

    // Each matching column joined to its owning object for the display name (a soft link on ObjectKey; no FK). A
    // token may match either the column or the object it sits on, so "ferrypassengers sourcerank" finds the one
    // column on the one table without the caller having to know which word names which.
    internal static IQueryable<ColumnHitDto> ColumnsQuery(CatalogDbContext db, SearchQuery term)
    {
        var rows = from c in db.ObjectColumns.AsNoTracking()
                   join o in db.Objects.AsNoTracking() on c.ObjectKey equals o.Key
                   select new { Column = c, Object = o };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Column.Name.Contains(t) || x.Object.Name.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(x => x.Column.Name.Contains(phrase))
            .ThenBy(x => x.Column.Name).ThenBy(x => x.Object.Name).ThenBy(x => x.Column.Ordinal)
            .Select(x => new ColumnHitDto(
                x.Column.ObjectKey, x.Object.Name, x.Column.Name, x.Column.DataType, x.Column.Nullable));
    }

    // Only objects that carry a module body or an emitted script can match; the scan runs server-side over both.
    internal static IQueryable<DefinitionRow> DefinitionRows(CatalogDbContext db, SearchQuery term)
    {
        var rows = db.Objects.AsNoTracking()
            .Where(o => o.Definition != null || o.Script != null);

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(o => (o.Definition != null && o.Definition.Contains(t))
                || (o.Script != null && o.Script.Contains(t)));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(o => (o.Definition != null && o.Definition.Contains(phrase))
                || (o.Script != null && o.Script.Contains(phrase)))
            .ThenBy(o => o.Name).ThenBy(o => o.Key)
            .Select(o => new DefinitionRow(o.Key, o.Name, o.Kind, o.Definition, o.Script));
    }

    // Each processed file joined to the run that touched it, newest run first within a file name.
    internal static IQueryable<FileHitDto> FilesQuery(CatalogDbContext db, SearchQuery term)
    {
        var rows = from f in db.RunFiles.AsNoTracking()
                   join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
                   // Left join the run's repo so the hit can name where its flow lives (a run may carry no repo).
                   join repo in db.Repos.AsNoTracking() on r.RepoId equals repo.Id into repoJoin
                   from repo in repoJoin.DefaultIfEmpty()
                   select new { File = f, Run = r, Repo = repo };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.File.Name.Contains(t) || (x.File.Path != null && x.File.Path.Contains(t)));
        }

        var phrase = term.Phrase;
        // Guid.ToString() here is translated to SQL Server's CONVERT, which yields UPPERCASE; the rest of the API
        // emits lowercase GUIDs (System.Text.Json), and the GUI matches graph node ids by exact string. Lowercase
        // the ids the client keys on (pipeline, repo) so a file's lineage/pipeline jump resolves.
        // ToLower() runs entirely server-side (translated to SQL LOWER), so the current-culture concern CA1304/CA1311
        // raise does not apply, and the invariant overloads are not guaranteed to translate; suppress here.
#pragma warning disable CA1304, CA1311
        return rows
            .OrderByDescending(x => x.File.Name.Contains(phrase))
            .ThenBy(x => x.File.Name).ThenByDescending(x => x.Run.StartUtc)
            .Select(x => new FileHitDto(
                x.File.Name, x.File.Path, x.Run.RunId.ToString().ToLower(), x.Run.FlowName, x.Run.FlowKind,
                x.File.Rows, x.File.Columns, x.File.SizeBytes, x.Run.StartUtc,
                x.Run.PipelineId.ToString().ToLower(),
                x.Run.RepoId.HasValue ? x.Run.RepoId.Value.ToString().ToLower() : null,
                x.Repo != null ? x.Repo.Name : null));
#pragma warning restore CA1304, CA1311
    }

    // A flow matches on its name, its repo-relative path, or a term anywhere in its YAML body.
    internal static IQueryable<FlowRow> FlowRows(CatalogDbContext db, SearchQuery term)
    {
        var rows = from p in db.Pipelines.AsNoTracking()
                   join repo in db.Repos.AsNoTracking() on p.RepoId equals repo.Id
                   select new { Pipeline = p, Repo = repo };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Pipeline.Name.Contains(t)
                || x.Pipeline.RelativePath.Contains(t)
                || x.Pipeline.Yaml.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(x => x.Pipeline.Name.Contains(phrase))
            .ThenBy(x => x.Pipeline.Name).ThenBy(x => x.Pipeline.Id)
            .Select(x => new FlowRow(
                x.Pipeline.Id, x.Pipeline.Name, x.Pipeline.Kind, x.Pipeline.Batch, x.Pipeline.RelativePath,
                x.Pipeline.RepoId, x.Repo.Name, x.Pipeline.Yaml));
    }

    // The columns flows produce: the output name, the raw source column behind it, and the expression computing it,
    // each joined to the owning flow and repo so a hit names the pipeline to open next.
    internal static IQueryable<FlowColumnRow> FlowColumnRows(CatalogDbContext db, SearchQuery term)
    {
        var rows = from c in db.PipelineColumns.AsNoTracking()
                   join p in db.Pipelines.AsNoTracking() on c.PipelineId equals p.Id
                   join repo in db.Repos.AsNoTracking() on p.RepoId equals repo.Id
                   select new { Column = c, Pipeline = p, Repo = repo };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Column.ColumnName.Contains(t)
                || (x.Column.SourceColumn != null && x.Column.SourceColumn.Contains(t))
                || (x.Column.Expression != null && x.Column.Expression.Contains(t))
                || x.Pipeline.Name.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(x => x.Column.ColumnName.Contains(phrase))
            .ThenBy(x => x.Column.ColumnName).ThenBy(x => x.Pipeline.Name)
            .ThenBy(x => x.Column.Kind).ThenBy(x => x.Column.Ordinal)
            .Select(x => new FlowColumnRow(
                x.Pipeline.Id, x.Pipeline.Name, x.Pipeline.Kind, x.Pipeline.Batch, x.Pipeline.RepoId, x.Repo.Name,
                x.Column.Kind, x.Column.Ordinal, x.Column.ColumnName, x.Column.SourceColumn, x.Column.DataType,
                x.Column.Expression));
    }

    /// <summary>The oldest statement timestamp a search reaches, or null when the window is "all history".</summary>
    internal static DateTime? StatementCutoff(TimeProvider clock, int windowDays)
        => windowDays <= 0 ? null : clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(windowDays);

    /// <summary>
    /// Executed statements matching the term, collapsed to one group per (flow, step) and ordered newest group
    /// first (callers page it as-is; see the ordering note inside). The engine emits the same
    /// statement shape every run, so grouping is what makes this surface readable: without it a term in a nightly
    /// merge returns one hit per night. Each group carries the newest matching statement's id, which the mapper
    /// resolves to its text in one round trip for the whole page.
    /// </summary>
    internal static IQueryable<StatementGroup> StatementGroups(
        CatalogDbContext db, SearchQuery term, DateTime? since)
    {
        var rows = from s in db.RunStatements.AsNoTracking()
                   join r in db.Runs.AsNoTracking() on s.RunId equals r.RunId
                   select new { Statement = s, Run = r };

        if (since is { } cutoff)
        {
            // A statement with no timestamp predates the timestamped trace, so it is older than any window.
            rows = rows.Where(x => x.Statement.TimestampUtc != null && x.Statement.TimestampUtc >= cutoff);
        }

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Statement.Sql.Contains(t)
                || x.Statement.Step.Contains(t)
                || x.Run.FlowName.Contains(t));
        }

        // Newest group first, ordered BEFORE the record projection: EF Core cannot see through a constructor
        // projection, so an OrderBy a caller chains onto the projected record fails to translate at runtime.
        // Ordering here on the anonymous shape keeps the whole query, including a caller's Skip/Take, in SQL.
        return rows
            .GroupBy(x => new { x.Run.PipelineId, x.Run.FlowName, x.Run.FlowKind, x.Statement.Step })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.FlowName,
                g.Key.FlowKind,
                g.Key.Step,
                Occurrences = g.LongCount(),
                LastStatementId = g.Max(x => x.Statement.Id),
                LastSeenUtc = g.Max(x => x.Statement.TimestampUtc),
            })
            .OrderByDescending(x => x.LastStatementId)
            .Select(x => new StatementGroup(
                x.PipelineId, x.FlowName, x.FlowKind, x.Step, x.Occurrences, x.LastStatementId, x.LastSeenUtc));
    }

    /// <summary>
    /// Attaches each group's sample SQL: one query fetches the newest matching statement of every group on the page
    /// (never one query per row), and the excerpt is then centered on the match in memory.
    /// </summary>
    private static async Task<List<StatementHitDto>> MapStatementsAsync(
        CatalogDbContext db, List<StatementGroup> groups, SearchQuery term, CancellationToken ct)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        var ids = groups.Select(g => g.LastStatementId).ToList();
        var samples = await db.RunStatements.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.RunId, s.Sql })
            .ToDictionaryAsync(s => s.Id, ct)
            .ConfigureAwait(false);

        return groups.Select(g =>
        {
            // The sample is read back in a second query, so a retention purge racing this search can remove it
            // between the two. The group still counts and locates the hit, so report it with an empty excerpt
            // rather than dropping a real match.
            var sample = samples.GetValueOrDefault(g.LastStatementId);
            return new StatementHitDto(
                g.PipelineId.ToString(), g.FlowName, g.FlowKind, g.Step, g.Occurrences,
                sample?.RunId.ToString() ?? string.Empty, g.LastSeenUtc,
                sample is null ? string.Empty : BuildSnippet(sample.Sql, term));
        }).ToList();
    }

    private static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query, int page, int size, CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<T>(items, page, size, total);
    }

    private static async Task<SearchCategoryDto<T>> PreviewAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false);
        return new SearchCategoryDto<T>(total, items);
    }

    private static DefinitionHitDto MapDefinition(DefinitionRow r, SearchQuery term)
    {
        // Prefer whichever body carries more of the tokens; a tie goes to the live module, which is the object as
        // it exists now rather than the DDL that once created it.
        var inModule = term.TokenHits(r.Definition) >= term.TokenHits(r.Script) && r.Definition is not null;
        var body = inModule ? r.Definition! : (r.Script ?? r.Definition ?? string.Empty);
        var source = inModule ? "Module" : "Script";
        return new DefinitionHitDto(r.Key, r.Name, r.Kind, BuildSnippet(body, term), source);
    }

    private static FlowHitDto MapFlow(FlowRow r, SearchQuery term)
    {
        // Name/path matches are self-explanatory; only a body match needs an excerpt to show why it matched. The
        // field carrying the most tokens wins, so a flow named for the source and mentioning the term once in its
        // YAML still reads as a body hit.
        var nameHits = term.TokenHits(r.Name);
        var pathHits = term.TokenHits(r.RelativePath);
        var yamlHits = term.TokenHits(r.Yaml);

        if (nameHits >= pathHits && nameHits >= yamlHits && nameHits > 0)
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Name", r.RelativePath);
        }

        if (pathHits >= yamlHits && pathHits > 0)
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Path", r.RelativePath);
        }

        return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
            r.RepoName, "Body", BuildSnippet(r.Yaml, term));
    }

    private static FlowColumnHitDto MapFlowColumn(FlowColumnRow r, SearchQuery term)
    {
        // Which part of the column carried the match: its own name, the raw source column it reads, or the SQL
        // expression that computes it. An expression hit is the interesting one - it means the term is COMPUTED
        // here rather than merely passed through.
        var matchedIn = term.TokenHits(r.ColumnName) > 0
            ? "Column"
            : term.TokenHits(r.SourceColumn) > 0 ? "Source" : "Expression";

        return new FlowColumnHitDto(
            r.PipelineId.ToString(), r.FlowName, r.FlowKind, r.Batch, r.RepoId.ToString(), r.RepoName,
            r.Kind, r.Ordinal, r.ColumnName, r.SourceColumn, r.DataType, Clip(r.Expression), matchedIn);
    }

    /// <summary>Caps a value at <see cref="SnippetMaxLength"/> so one enormous expression cannot dominate a page.</summary>
    private static string? Clip(string? text)
        => text is not null && text.Length > SnippetMaxLength
            ? string.Concat(text.AsSpan(0, SnippetMaxLength), "...")
            : text;

    /// <summary>
    /// Builds a bounded excerpt of <paramref name="text"/> centered on the verbatim phrase, or failing that on the
    /// earliest matching token, capped at <see cref="SnippetMaxLength"/> characters with ellipses where the text was
    /// trimmed.
    /// </summary>
    private static string BuildSnippet(string text, SearchQuery term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var (index, matchLength) = term.FirstMatch(text);
        if (index < 0)
        {
            // The row matched on a sibling field (or through a collation difference): show the leading slice rather
            // than failing.
            (index, matchLength) = (0, 0);
        }

        // Center the window on the match: keep some context on each side, clamped to the string's bounds.
        var lead = Math.Max(0, (SnippetMaxLength - matchLength) / 2);
        var start = Math.Max(0, index - lead);
        var length = Math.Min(SnippetMaxLength, text.Length - start);

        var slice = text.Substring(start, length);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < text.Length ? "..." : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    private static ProblemHttpResult BadRequest(string detail)
        => TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid search request");

    /// <summary>The columns a definition search needs from an object before the excerpt is built in memory.</summary>
    internal sealed record DefinitionRow(string Key, string Name, string Kind, string? Definition, string? Script);

    /// <summary>The columns a flow search needs from a pipeline before the match location and excerpt are computed.</summary>
    internal sealed record FlowRow(
        Guid Id, string Name, string Kind, string? Batch, string RelativePath, Guid RepoId, string RepoName, string Yaml);

    /// <summary>The columns a flow-column search needs before the match location is computed.</summary>
    internal sealed record FlowColumnRow(
        Guid PipelineId, string FlowName, string FlowKind, string? Batch, Guid RepoId, string RepoName,
        string Kind, int Ordinal, string ColumnName, string? SourceColumn, string? DataType, string? Expression);

    /// <summary>One (flow, step) group of matching statements: how many matched, and the newest one's id so its
    /// text can be fetched for the page in a single query.</summary>
    internal sealed record StatementGroup(
        Guid PipelineId, string FlowName, string FlowKind, string Step,
        long Occurrences, long LastStatementId, DateTime? LastSeenUtc);
}
