using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One allow-listed column of a semantic layer table, as an assistant is served it.</summary>
public sealed record SemanticColumnDto(
    int Ordinal, string Name, string? DataType, bool Nullable, bool IsKey, string? Description,
    IReadOnlyList<string> Synonyms);

/// <summary>One way to join a semantic layer table to another. <see cref="Source"/> is <c>Curated</c> (declared by
/// an admin) or <c>Discovered</c> (inferred from the codebase's own join predicates). Own/other columns pair by
/// position and <see cref="On"/> renders them ready to paste, aliased by table name.</summary>
public sealed record SemanticJoinDto(
    string Source, string OtherObjectKey, string? OtherDatabase, string? OtherSchema, string OtherName,
    IReadOnlyList<string> OwnColumns, IReadOnlyList<string> OtherColumns, string On,
    IReadOnlyList<string> JoinTypes, bool IsRangeJoin, int? Occurrences, string? Description);

/// <summary>A named, reusable SQL expression anchored to the table whose columns it reads.</summary>
public sealed record SemanticMeasureDto(
    long Id, string Name, string ObjectKey, string ObjectName, string Expression, string? Description);

/// <summary>A stored question with the SQL that already answers it, reading the table it is listed under.</summary>
public sealed record SemanticExampleDto(long Id, string Question, string Sql, string Provenance, DateTime ConfirmedUtc);

/// <summary>A Power BI measure or calculated column a report defines on a semantic layer table. <c>Expression</c> is
/// DAX, not SQL: mirror its logic rather than pasting it.</summary>
public sealed record SemanticReportFieldDto(string Name, string Expression, string? Description);

/// <summary>A relationship a Power BI model declares from a semantic layer table to another layer table, in warehouse
/// terms: the table's own column, the other table, and its column, each spelled as the catalog spells it.
/// <c>IsActive</c> false means it applies only where a measure invokes it with USERELATIONSHIP. <c>ModelTable</c> and
/// <c>OtherModelTable</c> are the report's names for the two tables, as its DAX refers to them.</summary>
public sealed record SemanticReportRelationshipDto(
    string OwnColumn, string OtherObjectKey, string OtherName, string OtherColumn, string? Cardinality, bool IsActive,
    string ModelTable, string OtherModelTable);

/// <summary>One Power BI report's model table that loads from a semantic layer table, reduced to what the column
/// allow-list lets through: the measures and calculated columns whose every column is allowed, and the relationships
/// whose both tables are in the layer and both columns allowed.</summary>
public sealed record SemanticReportModelDto(
    string SubscriberKey, string SubscriberName, string ReportFile, string ModelTable,
    IReadOnlyList<SemanticReportFieldDto> Measures, IReadOnlyList<SemanticReportFieldDto> CalculatedColumns,
    IReadOnlyList<SemanticReportRelationshipDto> Relationships);

/// <summary>One semantic layer table's whole grounding bundle: identity, business context, allowed columns, the
/// servable key, joins, measures, example queries, the Power BI models built on it, the reports and dashboards that
/// consume it, and the AI context that serves it with that context's instructions.</summary>
public sealed record SemanticTableDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? BusinessName, string? Description, IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> KeyColumns, string? KeyOrigin,
    IReadOnlyList<SemanticColumnDto> Columns,
    IReadOnlyList<SemanticJoinDto> Joins,
    IReadOnlyList<SemanticMeasureDto> Measures,
    IReadOnlyList<SemanticExampleDto> Examples,
    IReadOnlyList<SemanticReportModelDto> ReportModels,
    IReadOnlyList<ObjectSubscriberDto> Consumers,
    string? Context,
    string? Instructions);

/// <summary>One semantic layer table as an assistant writes SQL against it: what <see cref="SemanticTableDto"/> holds but
/// the reports that consume it and its context's instructions, which a question's grounding serves once per context
/// rather than once per table.</summary>
public sealed record SemanticGroundingTableDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? BusinessName, string? Description, IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> KeyColumns, string? KeyOrigin,
    IReadOnlyList<SemanticColumnDto> Columns,
    IReadOnlyList<SemanticJoinDto> Joins,
    IReadOnlyList<SemanticMeasureDto> Measures,
    IReadOnlyList<SemanticExampleDto> Examples,
    IReadOnlyList<SemanticReportModelDto> ReportModels,
    string? Context);

/// <summary>An approved AI context's instructions, to read and follow whenever SQL is written over its tables.</summary>
public sealed record SemanticContextInstructionsDto(string Name, string Instructions);

/// <summary>The semantic layer a question is answered from, served beside its saved answers when none of them is
/// trusted: the tables it needs (the whole layer when it has three tables or fewer, else the ones its words mention
/// most), the instructions of the contexts serving them, and how many tables the layer holds in all, so a caller knows
/// whether a table the grounding does not hold may still be found by searching the layer.</summary>
public sealed record SemanticGroundingDto(
    int LayerTableCount, IReadOnlyList<SemanticContextInstructionsDto> Contexts,
    IReadOnlyList<SemanticGroundingTableDto> Tables);

/// <summary>One semantic layer table in a list: identity, business context, and how many allowed columns it has.</summary>
public sealed record SemanticTableSummaryDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? BusinessName, string? Description, IReadOnlyList<string> Synonyms, int ColumnCount);

/// <summary>A (database, schema) that holds semantic layer tables, with how many.</summary>
public sealed record SemanticSchemaDto(string? Database, string? Schema, int TableCount);

/// <summary>One approved AI context as the assistant sees it: what it covers and how many tables it serves.</summary>
public sealed record SemanticContextDto(string Name, string? Description, int TableCount);

/// <summary>The semantic layer at a glance: the approved AI contexts it is made of, the instructions to read first
/// (each headed by its context), where its tables live, and every servable measure.</summary>
public sealed record SemanticOverviewDto(
    IReadOnlyList<SemanticContextDto> Contexts, string? Instructions, int TableCount, IReadOnlyList<SemanticSchemaDto> Schemas,
    IReadOnlyList<SemanticMeasureDto> Measures);

/// <summary>A table matching a semantic layer search: the table, which of its allowed columns matched, and which
/// of its fields carried the match (<c>name</c>, <c>schema</c>, <c>businessName</c>, <c>description</c>,
/// <c>synonyms</c>, <c>columns</c>).</summary>
public sealed record SemanticTableHitDto(
    SemanticTableSummaryDto Table, IReadOnlyList<SemanticColumnDto> MatchedColumns, IReadOnlyList<string> MatchedOn);

/// <summary>A semantic layer search result: matching tables (paged) and matching measures.</summary>
public sealed record SemanticSearchDto(
    string Phrase, IReadOnlyList<string> Tokens, PagedResult<SemanticTableHitDto> Tables,
    IReadOnlyList<SemanticMeasureDto> Measures);

/// <summary>
/// The semantic layer as an AI assistant reads it: the approved AI contexts, which are what it answers a question from
/// warehouse data with. Everything here is built from what those contexts serve (see <see cref="SemanticLayer"/>), so a
/// table no approved context opens does not exist on this surface, and a column it does not list is never named, not
/// even in a join, key, measure, or example. Under the read scope, like the catalog's other read surfaces.
/// </summary>
public static class SemanticLayerEndpoints
{
    /// <summary>The most columns one search hit lists as matched.</summary>
    private const int MaxMatchedColumns = 15;

    /// <summary>The most measures one search returns.</summary>
    private const int MaxSearchMeasures = 20;

    public static RouteGroupBuilder MapSemanticLayerEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var layer = group.MapGroup("/semantic-layer").WithTags("SemanticLayer");
        layer.MapGet(string.Empty, GetOverviewAsync).WithName("GetSemanticLayer");
        layer.MapGet("/search", SearchAsync).WithName("SearchSemanticLayer");
        layer.MapGet("/tables", ListTablesAsync).WithName("ListSemanticTables");
        layer.MapGet("/tables/describe", DescribeTableAsync).WithName("DescribeSemanticTable");

        return group;
    }

    private static async Task<Ok<SemanticOverviewDto>> GetOverviewAsync(CatalogDbContext db, CancellationToken ct)
    {
        var schemas = await SemanticLayer.LayerObjects(db)
            .GroupBy(o => new { o.Database, o.Schema })
            .Select(g => new { g.Key.Database, g.Key.Schema, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var ordered = schemas
            .Select(s => new SemanticSchemaDto(s.Database, s.Schema, s.Count))
            .OrderBy(s => s.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Schema, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var measures = (await SemanticLayer.EvaluateMeasuresAsync(db, db.SemanticMeasures, ct).ConfigureAwait(false))
            .Where(m => m.Problem is null)
            .Select(m => m.ToDto())
            .ToList();

        var instructions = await SemanticLayer.LoadInstructionsAsync(db, ct).ConfigureAwait(false);

        // The contexts the layer is made of, so the assistant can tell which area a question belongs to before it
        // searches: each with the number of tables it serves now (narrowed, it may serve fewer than it was approved with).
        var contexts = await db.AiContexts.AsNoTracking()
            .Where(c => c.ApprovedHash != null)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Name,
                c.Description,
                Tables = db.SemanticObjects.Count(o => o.AiContext == c.Name),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new SemanticOverviewDto(
            contexts.Where(c => c.Tables > 0).Select(c => new SemanticContextDto(c.Name, c.Description, c.Tables)).ToList(),
            instructions, ordered.Sum(s => s.TableCount), ordered, measures));
    }

    private static async Task<Ok<PagedResult<SemanticTableSummaryDto>>> ListTablesAsync(
        CatalogDbContext db, string? database, string? schema, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var objects = SemanticLayer.LayerObjects(db);
        if (!string.IsNullOrWhiteSpace(database))
        {
            objects = objects.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            objects = objects.Where(o => o.Schema == schema);
        }

        var total = await objects.LongCountAsync(ct).ConfigureAwait(false);
        var keys = await objects
            .OrderBy(o => o.Database).ThenBy(o => o.Schema).ThenBy(o => o.Name).ThenBy(o => o.Key)
            .Skip((p - 1) * size).Take(size)
            .Select(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);

        var summaries = await SemanticLayer.LoadSummariesAsync(db, keys, ct).ConfigureAwait(false);
        var items = keys.Where(summaries.ContainsKey).Select(k => summaries[k].Summary).ToList();
        return TypedResults.Ok(new PagedResult<SemanticTableSummaryDto>(items, p, size, total));
    }

    /// <summary>One table's bundle. <paramref name="question"/>, when given, is the question being answered: the bundle's
    /// examples are then the ones close to it, cut by the saved answers' coverage floor, rather than the newest.</summary>
    private static async Task<Results<Ok<SemanticTableDto>, ProblemHttpResult>> DescribeTableAsync(
        string? key, string? question, CatalogDbContext db, IOptions<ControlPlaneOptions> options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (a table key from search_semantic_layer or list_semantic_tables).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (question is { Length: > SemanticLayer.MaxQuestionLength })
        {
            return TypedResults.Problem(
                detail: $"The 'question' query parameter is {question.Length} characters; a question is at most " +
                    $"{SemanticLayer.MaxQuestionLength}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var closestTo = string.IsNullOrWhiteSpace(question)
            ? null
            : new SemanticLayer.ExampleQuestion(
                question.Trim(), [], options.Value.AiContext.SavedAnswers.MinCoverage, new HashSet<long>());
        var table = await SemanticLayer.DescribeTableAsync(db, key.Trim(), closestTo, ct).ConfigureAwait(false);
        return table is null
            ? TypedResults.Problem(
                detail: $"'{key}' is not a table in the semantic layer. Only tables with at least one allow-listed " +
                    "column exist here; find one with search_semantic_layer.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(table);
    }

    /// <summary>
    /// Token-AND search over the layer: every token must occur somewhere on the table, in its name, schema, business
    /// name, description, or synonyms, or in an allowed column's name, description, or synonyms. So "turnover region"
    /// finds the one table whose NetRevenue column has the synonym "turnover" and whose Region column exists. Tables
    /// whose own name carries the whole phrase rank first, then those whose business name does. Measures are matched
    /// on name, description, and expression under the same rule.
    /// </summary>
    private static async Task<Results<Ok<SemanticSearchDto>, ProblemHttpResult>> SearchAsync(
        string? q, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return TypedResults.Problem(
                detail: "The 'q' query parameter must carry at least one word of two or more characters.",
                statusCode: StatusCodes.Status400BadRequest, title: "Empty search");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);

        var rows = SemanticLayer.LayerRows(db);
        foreach (var token in term.Tokens)
        {
            rows = rows.Where(SemanticLayer.Mentions(db, token));
        }

        var phrase = term.Phrase;
        var total = await rows.LongCountAsync(ct).ConfigureAwait(false);
        var keys = await rows
            .OrderByDescending(x => x.Object.Name.Contains(phrase))
            .ThenByDescending(x => x.Annotation != null && x.Annotation.BusinessName != null
                && x.Annotation.BusinessName.Contains(phrase))
            .ThenBy(x => x.Object.Name).ThenBy(x => x.Object.Key)
            .Skip((p - 1) * size).Take(size)
            .Select(x => x.Object.Key)
            .ToListAsync(ct).ConfigureAwait(false);

        var summaries = await SemanticLayer.LoadSummariesAsync(db, keys, ct).ConfigureAwait(false);
        var hits = new List<SemanticTableHitDto>(keys.Count);
        foreach (var key in keys)
        {
            if (!summaries.TryGetValue(key, out var loaded))
            {
                continue;
            }

            var summary = loaded.Summary;
            var matchedColumns = loaded.Columns
                .Where(c => term.TokenHits(c.Name) > 0
                    || term.TokenHits(c.Description) > 0
                    || c.Synonyms.Any(s => term.TokenHits(s) > 0))
                .Take(MaxMatchedColumns)
                .Select(c => new SemanticColumnDto(c.Ordinal, c.Name, c.DataType, c.Nullable, IsKey: false, c.Description, c.Synonyms))
                .ToList();

            var matchedOn = new List<string>();
            if (term.TokenHits(summary.Name) > 0) { matchedOn.Add("name"); }
            if (term.TokenHits(summary.Schema) > 0) { matchedOn.Add("schema"); }
            if (term.TokenHits(summary.BusinessName) > 0) { matchedOn.Add("businessName"); }
            if (term.TokenHits(summary.Description) > 0) { matchedOn.Add("description"); }
            if (summary.Synonyms.Any(s => term.TokenHits(s) > 0)) { matchedOn.Add("synonyms"); }
            if (matchedColumns.Count > 0) { matchedOn.Add("columns"); }

            hits.Add(new SemanticTableHitDto(summary, matchedColumns, matchedOn));
        }

        var measureQuery = db.SemanticMeasures.AsNoTracking();
        foreach (var token in term.Tokens)
        {
            var t = token;
            measureQuery = measureQuery.Where(m => m.Name.Contains(t)
                || (m.Description != null && m.Description.Contains(t))
                || m.Expression.Contains(t));
        }

        var measures = (await SemanticLayer.EvaluateMeasuresAsync(db, measureQuery, ct).ConfigureAwait(false))
            .Where(m => m.Problem is null)
            .Take(MaxSearchMeasures)
            .Select(m => m.ToDto())
            .ToList();

        return TypedResults.Ok(new SemanticSearchDto(
            term.Phrase, term.Tokens, new PagedResult<SemanticTableHitDto>(hits, p, size, total), measures));
    }
}
