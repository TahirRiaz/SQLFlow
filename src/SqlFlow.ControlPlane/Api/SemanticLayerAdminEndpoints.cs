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

/// <summary>One object with catalogued columns, with how much of it is in the semantic layer.</summary>
public sealed record SemanticObjectCoverageDto(
    string Key, string Name, string Kind, string ServerRef, string? Database, string? Schema, string? BusinessName,
    int AllowedColumns, int TotalColumns);

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

/// <summary>Everything the semantic layer editor shows for one object.</summary>
public sealed record SemanticObjectAdminDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? InterpretedKeyColumns, string? InterpretedKeyOrigin,
    SemanticAnnotationDto Annotation,
    IReadOnlyList<ColumnPolicyStateDto> Columns,
    IReadOnlyList<SemanticCuratedJoinDto> CuratedJoins,
    IReadOnlyList<SemanticDiscoveredJoinDto> DiscoveredJoins,
    IReadOnlyList<SemanticMeasureAdminDto> Measures,
    IReadOnlyList<SemanticExampleAdminDto> Examples,
    IReadOnlyList<SemanticReportModelAdminDto> ReportModels);

/// <summary>Replaces an object's table-level business context. Every field is written as given (a null or empty
/// value clears it); when all are empty the annotation is removed.</summary>
public sealed record SetSemanticAnnotationRequest(
    string? ObjectKey, string? BusinessName, string? Description, IReadOnlyList<string>? Synonyms,
    IReadOnlyList<string>? KeyColumns);

/// <summary>The layer-wide instructions and their audit stamp.</summary>
public sealed record SemanticInstructionsDto(string? Instructions, string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>Replaces the layer-wide instructions (null or blank clears them).</summary>
public sealed record SetSemanticInstructionsRequest(string? Instructions);

/// <summary>Creates or replaces a measure.</summary>
public sealed record UpsertSemanticMeasureRequest(string? Name, string? ObjectKey, string? Expression, string? Description);

/// <summary>Creates or replaces a curated join. Columns pair by position.</summary>
public sealed record UpsertSemanticJoinRequest(
    string? FromObjectKey, IReadOnlyList<string>? FromColumns, string? ToObjectKey, IReadOnlyList<string>? ToColumns,
    string? JoinType, string? Description);

/// <summary>
/// The semantic layer editor: the admin-scope write side of what <see cref="SemanticLayerEndpoints"/> serves. The
/// allow-list itself stays in <see cref="ColumnPolicyEndpoints"/>; this adds the business context layered on top of
/// it and the coverage views the editor browses by. Every write is checked against the allow-list as it stands, so
/// an admin cannot declare a key, join, or measure over a column the layer does not contain.
/// </summary>
public static class SemanticLayerAdminEndpoints
{
    private const int MaxBusinessNameLength = 200;
    private const int MaxObjectDescriptionLength = 4000;
    private const int MaxJoinOrMeasureDescriptionLength = 1000;
    private const int MaxStoredColumnListLength = 1024;
    private const int MaxExpressionLength = 4000;

    public static RouteGroupBuilder MapSemanticLayerAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var layer = group.MapGroup("/powerai/semantic-layer").WithTags("PowerAI");
        layer.MapGet("/schemas", ListSchemasAsync).WithName("ListSemanticLayerSchemaCoverage");
        layer.MapGet("/objects", ListObjectsAsync).WithName("ListSemanticLayerObjectCoverage");
        layer.MapGet("/objects/detail", GetObjectAsync).WithName("GetSemanticLayerObject");
        layer.MapPut("/objects/annotation", SetAnnotationAsync).WithName("SetSemanticLayerAnnotation");
        layer.MapGet("/instructions", GetInstructionsAsync).WithName("GetSemanticLayerInstructions");
        layer.MapPut("/instructions", SetInstructionsAsync).WithName("SetSemanticLayerInstructions");
        layer.MapGet("/measures", ListMeasuresAsync).WithName("ListSemanticLayerMeasures");
        layer.MapPost("/measures", CreateMeasureAsync).WithName("CreateSemanticLayerMeasure");
        layer.MapPut("/measures/{id:long}", UpdateMeasureAsync).WithName("UpdateSemanticLayerMeasure");
        layer.MapDelete("/measures/{id:long}", DeleteMeasureAsync).WithName("DeleteSemanticLayerMeasure");
        layer.MapPost("/relationships", CreateJoinAsync).WithName("CreateSemanticLayerRelationship");
        layer.MapPut("/relationships/{id:long}", UpdateJoinAsync).WithName("UpdateSemanticLayerRelationship");
        layer.MapDelete("/relationships/{id:long}", DeleteJoinAsync).WithName("DeleteSemanticLayerRelationship");

        return group;
    }

    /// <summary>Coverage per (database, schema), over objects that have catalogued columns (the only objects a
    /// column policy can apply to). Two grouped reads merged in memory, since both are bounded by the schema count.</summary>
    private static async Task<Ok<IReadOnlyList<SemanticSchemaCoverageDto>>> ListSchemasAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var columnar = db.Objects.AsNoTracking().Where(o => db.ObjectColumns.Any(c => c.ObjectKey == o.Key));
        var all = await columnar
            .GroupBy(o => new { o.Database, o.Schema })
            .Select(g => new { g.Key.Database, g.Key.Schema, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var layerKeys = SemanticLayer.LayerObjectKeys(db);
        var inLayer = await columnar
            .Where(o => layerKeys.Contains(o.Key))
            .GroupBy(o => new { o.Database, o.Schema })
            .Select(g => new { g.Key.Database, g.Key.Schema, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = all
            .Select(a => new SemanticSchemaCoverageDto(
                a.Database, a.Schema, a.Count,
                inLayer.Find(l => l.Database == a.Database && l.Schema == a.Schema)?.Count ?? 0))
            .OrderBy(r => r.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Schema, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SemanticSchemaCoverageDto>>(rows);
    }

    private static async Task<Ok<PagedResult<SemanticObjectCoverageDto>>> ListObjectsAsync(
        CatalogDbContext db, string? database, string? schema, string? name, bool? inLayer, int? page, int? pageSize,
        CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.Objects.AsNoTracking().Where(o => db.ObjectColumns.Any(c => c.ObjectKey == o.Key));
        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            query = query.Where(o => o.Schema == schema);
        }

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
                db.ObjectColumns.Count(c => c.ObjectKey == o.Key)))
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

        var examples = (await SemanticLayer.EvaluateExamplesAsync(db, key, SemanticLayer.MaxAdminExamples, servableOnly: false, ct)
                .ConfigureAwait(false))
            .Select(e => e.ToAdminDto())
            .ToList();

        var reportModels = (await SemanticLayer.EvaluateReportModelsAsync(db, key, ct).ConfigureAwait(false))
            .Select(m => m.ToAdminDto())
            .ToList();

        return TypedResults.Ok(new SemanticObjectAdminDto(
            obj.Key, obj.ServerRef, obj.Database, obj.Schema, obj.Name, obj.Kind, obj.KeyColumns, obj.KeyOrigin,
            ToAnnotationDto(annotation), columns, curated, discovered, measures, examples, reportModels));
    }

    private static async Task<Results<Ok<SemanticAnnotationDto>, ProblemHttpResult>> SetAnnotationAsync(
        SetSemanticAnnotationRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ObjectKey))
        {
            return Invalid("objectKey is required.");
        }

        var key = request.ObjectKey.Trim();
        var objectName = await db.Objects.AsNoTracking()
            .Where(o => o.Key == key).Select(o => o.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (objectName is null)
        {
            return NotFound("object", key);
        }

        var businessName = SemanticLayer.TrimToNull(request.BusinessName);
        if (businessName is { Length: > MaxBusinessNameLength })
        {
            return Invalid($"businessName is longer than {MaxBusinessNameLength} characters.");
        }

        var description = SemanticLayer.TrimToNull(request.Description);
        if (description is { Length: > MaxObjectDescriptionLength })
        {
            return Invalid($"description is longer than {MaxObjectDescriptionLength} characters.");
        }

        if (!SemanticLayer.TryJoinSynonyms(request.Synonyms, out var synonyms, out var synonymProblem))
        {
            return Invalid(synonymProblem!);
        }

        if (!SemanticLayer.TryNormalizeColumns(request.KeyColumns, "keyColumns", out var keyColumns, out var keyProblem))
        {
            return Invalid(keyProblem!);
        }

        string? storedKey = null;
        if (keyColumns.Count > 0)
        {
            var allowed = await SemanticLayer.LoadAllowedColumnsAsync(db, [key], ct).ConfigureAwait(false);
            allowed.TryGetValue(key, out var objectAllowed);
            var problem = SemanticLayer.ColumnsProblem(objectName, keyColumns, objectAllowed);
            if (problem is not null)
            {
                return Invalid(problem);
            }

            storedKey = string.Join(',', SemanticLayer.Canonical(keyColumns, objectAllowed!));
            if (storedKey.Length > MaxStoredColumnListLength)
            {
                return Invalid($"keyColumns together exceed {MaxStoredColumnListLength} characters.");
            }
        }

        var actor = SemanticLayer.Actor(user);
        var now = clock.GetUtcNow().UtcDateTime;
        var empty = businessName is null && description is null && synonyms is null && storedKey is null;

        // Two saves racing on an object with no annotation yet both try to insert; the unique index decides, and the
        // loser applies its values to the winner's row on the second pass rather than failing a save that is valid.
        for (var attempt = 0; ; attempt++)
        {
            // Tracked explicitly: the catalog context defaults to no-tracking, which would drop the update.
            var row = await db.SemanticObjects.AsTracking()
                .FirstOrDefaultAsync(s => s.ObjectKey == key, ct).ConfigureAwait(false);
            if (empty)
            {
                if (row is not null)
                {
                    db.SemanticObjects.Remove(row);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return TypedResults.Ok(ToAnnotationDto(null));
            }

            if (row is null)
            {
                row = new CatalogSemanticObject { ObjectKey = key };
                db.SemanticObjects.Add(row);
            }

            row.BusinessName = businessName;
            row.Description = description;
            row.Synonyms = synonyms;
            row.KeyColumns = storedKey;
            row.UpdatedBy = actor;
            row.UpdatedUtc = now;

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return TypedResults.Ok(ToAnnotationDto(row));
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<Ok<SemanticInstructionsDto>> GetInstructionsAsync(CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.SemanticLayerSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == CatalogSemanticLayerSettings.SingletonId, ct).ConfigureAwait(false);
        return TypedResults.Ok(new SemanticInstructionsDto(row?.Instructions, row?.UpdatedBy, row?.UpdatedUtc));
    }

    private static async Task<Results<Ok<SemanticInstructionsDto>, ProblemHttpResult>> SetInstructionsAsync(
        SetSemanticInstructionsRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        var instructions = SemanticLayer.TrimToNull(request.Instructions);
        if (instructions is { Length: > SemanticLayer.MaxInstructionsLength })
        {
            return Invalid($"instructions are longer than {SemanticLayer.MaxInstructionsLength} characters.");
        }

        var actor = SemanticLayer.Actor(user);
        var now = clock.GetUtcNow().UtcDateTime;
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.SemanticLayerSettings.AsTracking()
                .FirstOrDefaultAsync(s => s.Id == CatalogSemanticLayerSettings.SingletonId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new CatalogSemanticLayerSettings { Id = CatalogSemanticLayerSettings.SingletonId };
                db.SemanticLayerSettings.Add(row);
            }

            row.Instructions = instructions;
            row.UpdatedBy = actor;
            row.UpdatedUtc = now;

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return TypedResults.Ok(new SemanticInstructionsDto(row.Instructions, row.UpdatedBy, row.UpdatedUtc));
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another save inserted the singleton first; update that row instead.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<Ok<IReadOnlyList<SemanticMeasureAdminDto>>> ListMeasuresAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var measures = await SemanticLayer.EvaluateMeasuresAsync(db, db.SemanticMeasures, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<SemanticMeasureAdminDto>>(measures.Select(ToAdminDto).ToList());
    }

    private static Task<Results<Ok<SemanticMeasureAdminDto>, ProblemHttpResult>> CreateMeasureAsync(
        UpsertSemanticMeasureRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
        => SaveMeasureAsync(null, request, db, clock, user, ct);

    private static Task<Results<Ok<SemanticMeasureAdminDto>, ProblemHttpResult>> UpdateMeasureAsync(
        long id, UpsertSemanticMeasureRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
        => SaveMeasureAsync(id, request, db, clock, user, ct);

    private static async Task<Results<Ok<SemanticMeasureAdminDto>, ProblemHttpResult>> SaveMeasureAsync(
        long? id, UpsertSemanticMeasureRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (!SemanticLayer.IsValidMeasureName(name))
        {
            return Invalid(
                "name must start with a letter or underscore and hold only letters, digits, and underscores " +
                "(at most 128 characters), for example net_revenue.");
        }

        if (string.IsNullOrWhiteSpace(request.ObjectKey))
        {
            return Invalid("objectKey (the table the expression reads) is required.");
        }

        var expression = (request.Expression ?? string.Empty).Trim();
        if (expression.Length == 0 || expression.Length > MaxExpressionLength)
        {
            return Invalid($"expression is required and must be at most {MaxExpressionLength} characters.");
        }

        var description = SemanticLayer.TrimToNull(request.Description);
        if (description is { Length: > MaxJoinOrMeasureDescriptionLength })
        {
            return Invalid($"description is longer than {MaxJoinOrMeasureDescriptionLength} characters.");
        }

        var objectKey = request.ObjectKey.Trim();
        var problem = await SemanticLayer.MeasureProblemAsync(db, objectKey, expression, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return TypedResults.Problem(detail: problem, statusCode: StatusCodes.Status400BadRequest, title: "Measure refused");
        }

        CatalogSemanticMeasure? measure;
        if (id is { } existingId)
        {
            measure = await db.SemanticMeasures.AsTracking()
                .FirstOrDefaultAsync(m => m.Id == existingId, ct).ConfigureAwait(false);
            if (measure is null)
            {
                return NotFound("measure", existingId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            measure = new CatalogSemanticMeasure();
            db.SemanticMeasures.Add(measure);
        }

        var ownId = id ?? 0;
        if (await db.SemanticMeasures.AsNoTracking().AnyAsync(m => m.Name == name && m.Id != ownId, ct).ConfigureAwait(false))
        {
            return MeasureNameTaken(name);
        }

        measure.Name = name;
        measure.ObjectKey = objectKey;
        measure.Expression = expression;
        measure.Description = description;
        measure.UpdatedBy = SemanticLayer.Actor(user);
        measure.UpdatedUtc = clock.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The name check above and the insert are not atomic: the unique index is what finally decides, and a
            // loss to a concurrent save of the same name is reported as the conflict it is. Anything else rethrows.
            db.ChangeTracker.Clear();
            if (await db.SemanticMeasures.AsNoTracking().AnyAsync(m => m.Name == name && m.Id != ownId, ct).ConfigureAwait(false))
            {
                return MeasureNameTaken(name);
            }

            throw;
        }

        var objectName = await db.Objects.AsNoTracking()
            .Where(o => o.Key == objectKey).Select(o => o.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(ToAdminDto(new SemanticLayer.MeasureEvaluation(measure, objectName ?? objectKey, null)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteMeasureAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var deleted = await db.SemanticMeasures.Where(m => m.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0
            ? NotFound("measure", id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : TypedResults.NoContent();
    }

    private static Task<Results<Ok<SemanticCuratedJoinDto>, ProblemHttpResult>> CreateJoinAsync(
        UpsertSemanticJoinRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
        => SaveJoinAsync(null, request, db, clock, user, ct);

    private static Task<Results<Ok<SemanticCuratedJoinDto>, ProblemHttpResult>> UpdateJoinAsync(
        long id, UpsertSemanticJoinRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
        => SaveJoinAsync(id, request, db, clock, user, ct);

    private static async Task<Results<Ok<SemanticCuratedJoinDto>, ProblemHttpResult>> SaveJoinAsync(
        long? id, UpsertSemanticJoinRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FromObjectKey) || string.IsNullOrWhiteSpace(request.ToObjectKey))
        {
            return Invalid("fromObjectKey and toObjectKey are both required.");
        }

        if (!SemanticLayer.TryNormalizeColumns(request.FromColumns, "fromColumns", out var fromColumns, out var fromProblem))
        {
            return Invalid(fromProblem!);
        }

        if (!SemanticLayer.TryNormalizeColumns(request.ToColumns, "toColumns", out var toColumns, out var toProblem))
        {
            return Invalid(toProblem!);
        }

        if (fromColumns.Count == 0 || fromColumns.Count != toColumns.Count)
        {
            return Invalid("fromColumns and toColumns must each name at least one column, and the same number of columns, paired by position.");
        }

        var joinType = SemanticJoinType.Normalize(request.JoinType ?? SemanticJoinType.Inner);
        if (joinType is null)
        {
            return Invalid($"joinType must be '{SemanticJoinType.Inner}' or '{SemanticJoinType.Left}'.");
        }

        var description = SemanticLayer.TrimToNull(request.Description);
        if (description is { Length: > MaxJoinOrMeasureDescriptionLength })
        {
            return Invalid($"description is longer than {MaxJoinOrMeasureDescriptionLength} characters.");
        }

        var fromKey = request.FromObjectKey.Trim();
        var toKey = request.ToObjectKey.Trim();
        var names = await db.Objects.AsNoTracking()
            .Where(o => o.Key == fromKey || o.Key == toKey)
            .Select(o => new { o.Key, o.Name })
            .ToDictionaryAsync(o => o.Key, o => o.Name, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        if (!names.TryGetValue(fromKey, out var fromName))
        {
            return NotFound("object", fromKey);
        }

        if (!names.TryGetValue(toKey, out var toName))
        {
            return NotFound("object", toKey);
        }

        var allowed = await SemanticLayer.LoadAllowedColumnsAsync(db, [fromKey, toKey], ct).ConfigureAwait(false);
        allowed.TryGetValue(fromKey, out var fromAllowed);
        allowed.TryGetValue(toKey, out var toAllowed);
        var columnProblem = SemanticLayer.ColumnsProblem(fromName, fromColumns, fromAllowed)
            ?? SemanticLayer.ColumnsProblem(toName, toColumns, toAllowed);
        if (columnProblem is not null)
        {
            return TypedResults.Problem(detail: columnProblem, statusCode: StatusCodes.Status400BadRequest, title: "Relationship refused");
        }

        var storedFrom = string.Join(',', SemanticLayer.Canonical(fromColumns, fromAllowed!));
        var storedTo = string.Join(',', SemanticLayer.Canonical(toColumns, toAllowed!));
        if (storedFrom.Length > MaxStoredColumnListLength || storedTo.Length > MaxStoredColumnListLength)
        {
            return Invalid($"A column list exceeds {MaxStoredColumnListLength} characters.");
        }

        var hash = SemanticLayer.JoinIdentity(
            fromKey, LineageEndpoints.SplitColumns(storedFrom), toKey, LineageEndpoints.SplitColumns(storedTo));

        CatalogSemanticRelationship? row;
        if (id is { } existingId)
        {
            row = await db.SemanticRelationships.AsTracking()
                .FirstOrDefaultAsync(r => r.Id == existingId, ct).ConfigureAwait(false);
            if (row is null)
            {
                return NotFound("relationship", existingId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            row = new CatalogSemanticRelationship();
            db.SemanticRelationships.Add(row);
        }

        var ownId = id ?? 0;
        if (await db.SemanticRelationships.AsNoTracking().AnyAsync(r => r.IdentityHash == hash && r.Id != ownId, ct).ConfigureAwait(false))
        {
            return JoinTaken();
        }

        row.FromObjectKey = fromKey;
        row.FromColumns = storedFrom;
        row.ToObjectKey = toKey;
        row.ToColumns = storedTo;
        row.JoinType = joinType;
        row.Description = description;
        row.IdentityHash = hash;
        row.UpdatedBy = SemanticLayer.Actor(user);
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Same reasoning as the measure name: the unique identity hash decides a concurrent duplicate.
            db.ChangeTracker.Clear();
            if (await db.SemanticRelationships.AsNoTracking().AnyAsync(r => r.IdentityHash == hash && r.Id != ownId, ct).ConfigureAwait(false))
            {
                return JoinTaken();
            }

            throw;
        }

        return TypedResults.Ok(new SemanticCuratedJoinDto(
            row.Id, fromKey, fromName, LineageEndpoints.SplitColumns(storedFrom),
            toKey, toName, LineageEndpoints.SplitColumns(storedTo),
            row.JoinType, row.Description, Problem: null, row.UpdatedBy, row.UpdatedUtc));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteJoinAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var deleted = await db.SemanticRelationships.Where(r => r.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0
            ? NotFound("relationship", id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : TypedResults.NoContent();
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

    private static ProblemHttpResult MeasureNameTaken(string name)
        => TypedResults.Problem(
            detail: $"A measure named '{name}' already exists. Measure names are unique across the semantic layer.",
            statusCode: StatusCodes.Status409Conflict, title: "Duplicate measure");

    private static ProblemHttpResult JoinTaken()
        => TypedResults.Problem(
            detail: "This relationship (the same tables on the same columns) is already declared.",
            statusCode: StatusCodes.Status409Conflict, title: "Duplicate relationship");

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(detail: $"No {resource} with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
