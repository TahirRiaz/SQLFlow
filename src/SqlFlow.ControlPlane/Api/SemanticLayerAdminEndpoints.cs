using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One (database, schema) of objects that have catalogued columns: how many there are, and how many are
/// in the semantic layer (at least one allowed column).</summary>
public sealed record SemanticSchemaCoverageDto(string? Database, string? Schema, int ObjectCount, int LayerObjectCount);

/// <summary>One object with catalogued columns, with how much of it is in the semantic layer and the AI context that
/// serves it, if any.</summary>
public sealed record SemanticObjectCoverageDto(
    string Key, string Name, string Kind, string ServerRef, string? Database, string? Schema, string? BusinessName,
    int AllowedColumns, int TotalColumns, string? Context);

/// <summary>An object's table-level business context as stored (the curated key is shown as saved, even when a
/// column of it has since been denied; the served key is decided at read time).</summary>
public sealed record SemanticAnnotationDto(
    string? BusinessName, string? Description, IReadOnlyList<string> Synonyms, IReadOnlyList<string> KeyColumns,
    string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>A curated join as the editor shows it, from its declared direction, with why it is not currently
/// served when it is not.</summary>
public sealed record SemanticCuratedJoinDto(
    long Id, string FromObjectKey, string FromObjectName, IReadOnlyList<string> FromColumns,
    string ToObjectKey, string ToObjectName, IReadOnlyList<string> ToColumns,
    string JoinType, string? Description, string? Problem, string? UpdatedBy, DateTime UpdatedUtc);

/// <summary>A join discovered from the codebase, read from the edited object's side, with why it is not served
/// when it is not.</summary>
public sealed record SemanticDiscoveredJoinDto(SemanticJoinDto Join, string? Problem);

/// <summary>A measure as the editor shows it, with why it is not currently served when it is not.</summary>
public sealed record SemanticMeasureAdminDto(
    long Id, string Name, string ObjectKey, string ObjectName, string Expression, string? Description,
    string? Problem, string? UpdatedBy, DateTime UpdatedUtc);

/// <summary>
/// A saved answer (one of the semantic layer's example queries) as the editor shows it, on a table's Examples tab and on
/// the Saved answers tab alike.
/// </summary>
/// <param name="Id">The example's id, the one <c>find_similar_questions</c> reports as <c>exampleId</c>.</param>
/// <param name="Question">The question it answers.</param>
/// <param name="Sql">The query that answers it.</param>
/// <param name="SourceRef">The datasource it runs against, or null when none is stored.</param>
/// <param name="ObjectKeys">The warehouse objects the query reads, which decide the tables it is an example of.</param>
/// <param name="Provenance">Where the example came from.</param>
/// <param name="Confidence">The retrieval score the answer was built from, when it was built from a match.</param>
/// <param name="RepoId">The repo it is attributed to, or null when estate-wide.</param>
/// <param name="ConfirmedBy">Who last stood behind it: the person who confirmed it, or the admin who last edited it.</param>
/// <param name="ConfirmedUtc">When that happened.</param>
/// <param name="Problem">Why the assistant is not currently offered it, or null when it is served.</param>
public sealed record SemanticExampleAdminDto(
    long Id, string Question, string Sql, string? SourceRef, IReadOnlyList<string> ObjectKeys, string Provenance,
    int? Confidence, Guid? RepoId, string? ConfirmedBy, DateTime ConfirmedUtc, string? Problem);

/// <summary>A Power BI measure or calculated column built on the edited object, with why it is not served when it is
/// not. <c>Kind</c> is <c>measure</c> or <c>calculatedColumn</c>; <c>Expression</c> is its DAX.</summary>
public sealed record SemanticReportFieldAdminDto(
    string Name, string Kind, string Expression, string? Description, string? Problem);

/// <summary>A Power BI model relationship from the edited object's model table, with the warehouse columns and object
/// it maps to (when they resolve) and why it is not served when it is not.</summary>
public sealed record SemanticReportRelationshipAdminDto(
    string ModelTable, string? OwnColumn, string OtherModelTable, string? OtherColumn, string? OtherObjectKey,
    string? Cardinality, bool IsActive, string? Problem);

/// <summary>One report's model table that loads from the edited object, with everything it defines and each item's
/// state. Read-only: the report is the source of these definitions.</summary>
public sealed record SemanticReportModelAdminDto(
    string SubscriberKey, string SubscriberName, string ReportFile, string ModelTable,
    IReadOnlyList<SemanticReportFieldAdminDto> Fields, IReadOnlyList<SemanticReportRelationshipAdminDto> Relationships);

/// <summary>Everything the AI context pages show for one object: what it serves and why anything is not served.</summary>
public sealed record SemanticObjectAdminDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? InterpretedKeyColumns, string? InterpretedKeyOrigin,
    SemanticAnnotationDto Annotation,
    IReadOnlyList<ColumnPolicyStateDto> Columns,
    IReadOnlyList<SemanticCuratedJoinDto> CuratedJoins,
    IReadOnlyList<SemanticDiscoveredJoinDto> DiscoveredJoins,
    IReadOnlyList<SemanticMeasureAdminDto> Measures,
    IReadOnlyList<SemanticExampleAdminDto> Examples,
    IReadOnlyList<SemanticReportModelAdminDto> ReportModels,
    string? Context);

/// <summary>
/// The AI context pages' view of the semantic layer, table by table: coverage per schema and per object, and one
/// object in full with what it serves and why anything is not served. Read-only: what the layer holds is written only by
/// approving an AI context (<see cref="AiContextEndpoints"/>), whose file is where it is edited.
/// </summary>
public static class SemanticLayerAdminEndpoints
{

    public static RouteGroupBuilder MapSemanticLayerAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var layer = group.MapGroup("/powerai/semantic-layer").WithTags("PowerAI");
        layer.MapGet("/schemas", ListSchemasAsync).WithName("ListSemanticLayerSchemaCoverage");
        layer.MapGet("/objects", ListObjectsAsync).WithName("ListSemanticLayerObjectCoverage");
        layer.MapGet("/objects/detail", GetObjectAsync).WithName("GetSemanticLayerObject");

        return group;
    }

    /// <summary>Coverage per (database, schema), over objects that have catalogued columns (the only objects a
    /// column policy can apply to), every server's objects in one tree by database. Names fold as the catalog browse folds
    /// them (<see cref="CatalogBrowse"/>): a database or schema spelled two ways that differ only in case is one row, shown
    /// in the spelling most of its objects carry. Two grouped reads merged in memory, since both are bounded by the schema
    /// count.</summary>
    private static async Task<Ok<IReadOnlyList<SemanticSchemaCoverageDto>>> ListSchemasAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        static List<SpelledGroup> BySchema(IEnumerable<SpelledGroup> groups)
            => CatalogBrowse.FoldSpellings(groups.Select(g => g with { ServerRef = string.Empty, Kind = string.Empty }).ToList());

        var columnar = db.Objects.AsNoTracking().Where(o => db.ObjectColumns.Any(c => c.ObjectKey == o.Key));
        var all = BySchema(await CatalogBrowse.GroupsAsync(columnar, ct).ConfigureAwait(false));

        var layerKeys = SemanticLayer.LayerObjectKeys(db);
        var inLayer = BySchema(await CatalogBrowse.GroupsAsync(columnar.Where(o => layerKeys.Contains(o.Key)), ct).ConfigureAwait(false))
            .ToDictionary(g => (CatalogBrowse.Fold(g.Database), CatalogBrowse.Fold(g.Schema)), g => g.Count);

        var rows = all
            .Select(a => new SemanticSchemaCoverageDto(
                a.Database, a.Schema, a.Count,
                inLayer.GetValueOrDefault((CatalogBrowse.Fold(a.Database), CatalogBrowse.Fold(a.Schema)))))
            .OrderBy(r => r.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Schema, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SemanticSchemaCoverageDto>>(rows);
    }

    private static async Task<Results<Ok<PagedResult<SemanticObjectCoverageDto>>, ProblemHttpResult>> ListObjectsAsync(
        CatalogDbContext db, string? database, string? schema, bool? databaseUnknown, bool? schemaUnknown, string? name,
        bool? inLayer, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        // The same filters the coverage counts with, so a schema's objects are exactly the ones it counted.
        var (browsed, problem) = CatalogBrowse.Filter(
            db.Objects.AsNoTracking().Where(o => db.ObjectColumns.Any(c => c.ObjectKey == o.Key)),
            serverRef: null, database, schema, databaseUnknown, schemaUnknown);
        if (browsed is null)
        {
            return problem!;
        }

        var query = browsed;

        var term = SearchQuery.Parse(name);
        if (term is not null)
        {
            foreach (var token in term.Tokens)
            {
                var t = token;
                query = query.Where(o => o.Name.Contains(t)
                    || db.SemanticObjects.Any(s => s.ObjectKey == o.Key && s.BusinessName != null && s.BusinessName.Contains(t)));
            }
        }

        if (inLayer == true)
        {
            var layerKeys = SemanticLayer.LayerObjectKeys(db);
            query = query.Where(o => layerKeys.Contains(o.Key));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(o => o.Name).ThenBy(o => o.Key)
            .Skip((p - 1) * size).Take(size)
            .Select(o => new SemanticObjectCoverageDto(
                o.Key, o.Name, o.Kind, o.ServerRef, o.Database, o.Schema,
                db.SemanticObjects.Where(s => s.ObjectKey == o.Key).Select(s => s.BusinessName).FirstOrDefault(),
                db.ColumnPolicies.Count(pol => pol.IsAllowed && pol.ObjectKey == o.Key
                    && db.ObjectColumns.Any(c => c.ObjectKey == pol.ObjectKey && c.Name == pol.ColumnName)),
                db.ObjectColumns.Count(c => c.ObjectKey == o.Key),
                db.SemanticObjects.Where(s => s.ObjectKey == o.Key).Select(s => s.AiContext).FirstOrDefault()))
            .ToListAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new PagedResult<SemanticObjectCoverageDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<SemanticObjectAdminDto>, ProblemHttpResult>> GetObjectAsync(
        string? key, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Invalid("A 'key' query parameter is required.");
        }

        key = key.Trim();
        var obj = await db.Objects.AsNoTracking()
            .Where(o => o.Key == key)
            .Select(o => new { o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.KeyColumns, o.KeyOrigin })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (obj is null)
        {
            return NotFound("object", key);
        }

        var annotation = await db.SemanticObjects.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ObjectKey == key, ct).ConfigureAwait(false);
        var columns = await ColumnPolicyEndpoints.LoadObjectStateAsync(db, key, ct).ConfigureAwait(false);

        var joins = await SemanticLayer.EvaluateJoinsAsync(db, key, obj.Name, ct).ConfigureAwait(false);
        var curated = joins.Where(j => j.Curated is not null).Select(j =>
        {
            var row = j.Curated!;
            var outgoing = string.Equals(row.FromObjectKey, key, StringComparison.Ordinal);
            return new SemanticCuratedJoinDto(
                row.Id, row.FromObjectKey, outgoing ? obj.Name : j.Join.OtherName,
                LineageEndpoints.SplitColumns(row.FromColumns),
                row.ToObjectKey, outgoing ? j.Join.OtherName : obj.Name,
                LineageEndpoints.SplitColumns(row.ToColumns),
                row.JoinType, row.Description, j.Problem, row.UpdatedBy, row.UpdatedUtc);
        }).ToList();
        var discovered = joins.Where(j => j.Discovered is not null)
            .Select(j => new SemanticDiscoveredJoinDto(j.Join, j.Problem))
            .ToList();

        var measures = (await SemanticLayer.EvaluateMeasuresAsync(db, db.SemanticMeasures.Where(m => m.ObjectKey == key), ct)
                .ConfigureAwait(false))
            .Select(ToAdminDto)
            .ToList();

        var examples = (await SemanticLayer.EvaluateExamplesAsync(
                db, key, SemanticLayer.MaxAdminExamples, servableOnly: false, closestTo: null, ct)
                .ConfigureAwait(false))
            .Select(e => e.ToAdminDto())
            .ToList();

        var reportModels = (await SemanticLayer.EvaluateReportModelsAsync(db, key, ct).ConfigureAwait(false))
            .Select(m => m.ToAdminDto())
            .ToList();

        return TypedResults.Ok(new SemanticObjectAdminDto(
            obj.Key, obj.ServerRef, obj.Database, obj.Schema, obj.Name, obj.Kind, obj.KeyColumns, obj.KeyOrigin,
            ToAnnotationDto(annotation), columns, curated, discovered, measures, examples, reportModels, annotation?.AiContext));
    }

    private static SemanticAnnotationDto ToAnnotationDto(CatalogSemanticObject? row)
        => row is null
            ? new SemanticAnnotationDto(null, null, [], [], null, null)
            : new SemanticAnnotationDto(
                row.BusinessName, row.Description, SemanticLayer.SplitSynonyms(row.Synonyms),
                LineageEndpoints.SplitColumns(row.KeyColumns ?? string.Empty), row.UpdatedBy, row.UpdatedUtc);

    private static SemanticMeasureAdminDto ToAdminDto(SemanticLayer.MeasureEvaluation evaluation)
        => new(
            evaluation.Measure.Id, evaluation.Measure.Name, evaluation.Measure.ObjectKey, evaluation.ObjectName,
            evaluation.Measure.Expression, evaluation.Measure.Description, evaluation.Problem,
            evaluation.Measure.UpdatedBy, evaluation.Measure.UpdatedUtc);

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(detail: $"No {resource} with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
