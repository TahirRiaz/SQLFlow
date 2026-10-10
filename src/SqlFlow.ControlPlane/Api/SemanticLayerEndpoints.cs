using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

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
/// consume it, and the layer-wide instructions.</summary>
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
    string? Instructions);

/// <summary>One semantic layer table in a list: identity, business context, and how many allowed columns it has.</summary>
public sealed record SemanticTableSummaryDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? BusinessName, string? Description, IReadOnlyList<string> Synonyms, int ColumnCount);

/// <summary>A (database, schema) that holds semantic layer tables, with how many.</summary>
public sealed record SemanticSchemaDto(string? Database, string? Schema, int TableCount);

/// <summary>The semantic layer at a glance: the instructions to read first, where its tables live, and every
/// servable measure.</summary>
public sealed record SemanticOverviewDto(
    string? Instructions, int TableCount, IReadOnlyList<SemanticSchemaDto> Schemas,
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
/// The semantic layer as an AI assistant reads it: the only schema surface the chat assistant is given. Everything
/// here is built from the column allow-list (see <see cref="SemanticLayer"/>), so a table with no allowed column does
/// not exist on this surface, and a column that is not allowed is never named, not even in a join, key, measure, or
/// example. Under the read scope, like the catalog's other read surfaces.
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
        return TypedResults.Ok(new SemanticOverviewDto(instructions, ordered.Sum(s => s.TableCount), ordered, measures));
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

        var summaries = await LoadSummariesAsync(db, keys, ct).ConfigureAwait(false);
        var items = keys.Where(summaries.ContainsKey).Select(k => summaries[k].Summary).ToList();
        return TypedResults.Ok(new PagedResult<SemanticTableSummaryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<SemanticTableDto>, ProblemHttpResult>> DescribeTableAsync(
        string? key, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (a table key from search_semantic_layer or list_semantic_tables).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var table = await SemanticLayer.DescribeTableAsync(db, key.Trim(), ct).ConfigureAwait(false);
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

        var rows = from o in SemanticLayer.LayerObjects(db)
                   join s in db.SemanticObjects.AsNoTracking() on o.Key equals s.ObjectKey into annotations
                   from s in annotations.DefaultIfEmpty()
                   select new { Object = o, Annotation = s };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Object.Name.Contains(t)
                || (x.Object.Schema != null && x.Object.Schema.Contains(t))
                || (x.Annotation != null
                    && ((x.Annotation.BusinessName != null && x.Annotation.BusinessName.Contains(t))
                        || (x.Annotation.Description != null && x.Annotation.Description.Contains(t))
                        || (x.Annotation.Synonyms != null && x.Annotation.Synonyms.Contains(t))))
                || db.ColumnPolicies.Any(pol => pol.IsAllowed && pol.ObjectKey == x.Object.Key
                    && (pol.ColumnName.Contains(t)
                        || (pol.Description != null && pol.Description.Contains(t))
                        || (pol.Synonyms != null && pol.Synonyms.Contains(t)))
                    && db.ObjectColumns.Any(c => c.ObjectKey == pol.ObjectKey && c.Name == pol.ColumnName)));
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

        var summaries = await LoadSummariesAsync(db, keys, ct).ConfigureAwait(false);
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

    /// <summary>The summary and allowed columns of each of <paramref name="keys"/> that is in the layer.</summary>
    private static async Task<Dictionary<string, (SemanticTableSummaryDto Summary, IReadOnlyList<SemanticLayer.AllowedColumn> Columns)>>
        LoadSummariesAsync(CatalogDbContext db, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var result = new Dictionary<string, (SemanticTableSummaryDto, IReadOnlyList<SemanticLayer.AllowedColumn>)>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var objects = await (from o in db.Objects.AsNoTracking()
                             where keys.Contains(o.Key)
                             join s in db.SemanticObjects.AsNoTracking() on o.Key equals s.ObjectKey into annotations
                             from s in annotations.DefaultIfEmpty()
                             select new
                             {
                                 o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind,
                                 BusinessName = s == null ? null : s.BusinessName,
                                 Description = s == null ? null : s.Description,
                                 Synonyms = s == null ? null : s.Synonyms,
                             })
            .ToListAsync(ct).ConfigureAwait(false);
        var allowed = await SemanticLayer.LoadAllowedColumnsAsync(db, keys, ct).ConfigureAwait(false);

        foreach (var o in objects)
        {
            if (!allowed.TryGetValue(o.Key, out var columns))
            {
                continue;
            }

            result[o.Key] = (
                new SemanticTableSummaryDto(
                    o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.BusinessName, o.Description,
                    SemanticLayer.SplitSynonyms(o.Synonyms), columns.Count),
                columns);
        }

        return result;
    }
}
