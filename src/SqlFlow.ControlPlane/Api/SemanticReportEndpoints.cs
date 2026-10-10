using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A Power BI subscriber a report can be attached to.</summary>
public sealed record SemanticReportSubscriberDto(
    Guid RepoId, string RepoName, string SubscriberKey, string Name, string Type, string? Owner);

/// <summary>A report specification the semantic layer holds, without its text.</summary>
/// <param name="Id">The row id.</param>
/// <param name="RepoId">The repo its subscriber belongs to.</param>
/// <param name="RepoName">That repo's name.</param>
/// <param name="SubscriberKey">The subscriber's node key.</param>
/// <param name="SubscriberName">The subscriber's name.</param>
/// <param name="SubscriberDeclared">False when the repository no longer declares the subscriber, so the report is
/// not being served.</param>
/// <param name="ReportFile">The report label.</param>
/// <param name="Origin"><c>upload</c> (a person's) or <c>extracted</c> (a sync's own copy of a declared report).</param>
/// <param name="Pages">Its page count.</param>
/// <param name="Visuals">Its question-asking visual count.</param>
/// <param name="Tables">Its model table count.</param>
/// <param name="Measures">Its measure count.</param>
/// <param name="UpdatedBy">Who stored it; null for a sync's copy.</param>
/// <param name="UpdatedUtc">When it was stored.</param>
public sealed record SemanticReportSpecDto(
    long Id, Guid RepoId, string RepoName, string SubscriberKey, string SubscriberName, bool SubscriberDeclared,
    string ReportFile, string Origin, int Pages, int Visuals, int Tables, int Measures, string? UpdatedBy,
    DateTime UpdatedUtc);

/// <summary>A stored report specification with its text and what reading it found.</summary>
/// <param name="Report">The stored report.</param>
/// <param name="Spec">The specification text.</param>
/// <param name="Summary">What reading the specification found.</param>
/// <param name="Pages">The pages and visuals the last sync served under this report's label, each visual with the
/// business questions generated for it. Empty until a sync has read the report, and for a subscriber the repository no
/// longer declares.</param>
public sealed record SemanticReportSpecDetailDto(
    SemanticReportSpecDto Report, string Spec, ReportSpecSummary Summary, IReadOnlyList<SubscriberReportPageDto> Pages);

/// <summary>Stores a report specification for a subscriber.</summary>
/// <param name="RepoId">The repo declaring the subscriber.</param>
/// <param name="Subscriber">The subscriber's name (or node key).</param>
/// <param name="ReportFile">The label the report is known by, normally its file name (<c>Sales.pbix</c>).</param>
/// <param name="Spec">The specification, as <c>sqlflow powerbi extract</c> or the extract endpoint produced it.</param>
public sealed record StoreSemanticReportSpecRequest(Guid? RepoId, string? Subscriber, string? ReportFile, string? Spec);

/// <summary>The outcome of storing a specification.</summary>
/// <param name="Report">The stored report.</param>
/// <param name="Replaced">True when it replaced an earlier upload of the same report.</param>
/// <param name="SyncQueued">True when the repo's managed sync was asked to apply it now; false for a repo synced from a
/// local path, which applies it on its next <c>sqlflow db sync</c>.</param>
public sealed record StoreSemanticReportSpecResult(SemanticReportSpecDetailDto Report, bool Replaced, bool SyncQueued);

/// <summary>A report the isolated extractor read, not yet stored.</summary>
public sealed record ExtractedSemanticReportDto(string ReportFile, string Spec, ReportSpecSummary Summary);

/// <summary>Adds a person's business question to a report visual.</summary>
/// <param name="RepoId">The repo the visual belongs to.</param>
/// <param name="VisualKey">The visual's key, as the report detail lists it.</param>
/// <param name="Question">The question text.</param>
public sealed record AddReportQuestionRequest(Guid? RepoId, string? VisualKey, string? Question);

/// <summary>Rewrites a business question. The question becomes a person's, so generation no longer replaces it.</summary>
public sealed record UpdateReportQuestionRequest(string? Question);

/// <summary>Sets whether a sync generates business questions for report visuals.</summary>
/// <param name="Enabled">True or false to decide, or null to follow the deployment's configured default.</param>
public sealed record SetQuestionGenerationRequest(bool? Enabled);

/// <summary>Whether a sync generates business questions for report visuals, and why.</summary>
/// <param name="Available">The deployment has an Anthropic key; without one generation cannot be turned on.</param>
/// <param name="DeploymentDefault"><c>ControlPlane:PowerAI:QuestionGeneration:Enabled</c>, followed while
/// <paramref name="Override"/> is null.</param>
/// <param name="Override">An admin's choice, or null.</param>
/// <param name="Enabled">Whether the next sync generates questions.</param>
/// <param name="UpdatedBy">Who last changed the switch.</param>
/// <param name="UpdatedUtc">When the switch was last changed.</param>
/// <param name="SyncsQueued">On a change that turned generation on, how many repos with report visuals had their
/// managed sync queued to generate the missing questions; otherwise 0.</param>
public sealed record QuestionGenerationDto(
    bool Available, bool DeploymentDefault, bool? Override, bool Enabled, string? UpdatedBy, DateTime? UpdatedUtc,
    int SyncsQueued);

/// <summary>The outcome of deleting a specification.</summary>
public sealed record DeleteSemanticReportSpecResult(bool SyncQueued);

/// <summary>What the report surface can do on this deployment.</summary>
/// <param name="ExtractionEnabled">Whether a raw <c>.pbix</c> can be uploaded (the isolated extractor is configured).</param>
/// <param name="MaxReportBytes">The largest <c>.pbix</c> accepted.</param>
/// <param name="MaxSpecBytes">The largest specification accepted.</param>
/// <param name="QuestionGenerationEnabled">Whether a sync generates business questions for report visuals
/// (<c>ControlPlane:PowerAI:QuestionGeneration:Enabled</c>).</param>
public sealed record SemanticReportCapabilitiesDto(
    bool ExtractionEnabled, long MaxReportBytes, int MaxSpecBytes, bool QuestionGenerationEnabled);

/// <summary>
/// The Power BI reports the semantic layer holds. A report reaches a subscriber's pages, visuals and model through a
/// specification, and this is where a person adds one the repository does not carry: upload a <c>.pbix</c> (extracted
/// in the isolated extractor, never here), or a specification made with <c>sqlflow powerbi extract</c>. Both end at
/// the one store endpoint, and a sync then reads the stored specification exactly as it reads a committed one.
/// <para>
/// Admin scope, like the rest of the semantic layer editor: a report's model and questions are what assistants answer
/// from, so adding or removing one is curation of the layer.
/// </para>
/// </summary>
public static class SemanticReportEndpoints
{
    /// <summary>The JSON store request carries a whole specification, so its body may be larger than the server's
    /// default; escaping can grow the text, hence the headroom over <see cref="ReportSpecs.MaxBytes"/>.</summary>
    private const long MaxStoreRequestBytes = 3L * ReportSpecs.MaxBytes;

    public static RouteGroupBuilder MapSemanticReportEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var reports = group.MapGroup("/powerai/semantic-layer/reports").WithTags("PowerAI");
        reports.MapGet("/capabilities", GetCapabilities).WithName("GetSemanticLayerReportCapabilities");
        reports.MapGet("/question-generation", GetQuestionGenerationAsync).WithName("GetSemanticLayerQuestionGeneration");
        reports.MapPut("/question-generation", SetQuestionGenerationAsync).WithName("SetSemanticLayerQuestionGeneration");
        reports.MapPost("/questions", AddQuestionAsync).WithName("AddSemanticLayerReportQuestion");
        reports.MapPut("/questions/{id:long}", UpdateQuestionAsync).WithName("UpdateSemanticLayerReportQuestion");
        reports.MapDelete("/questions/{id:long}", DeleteQuestionAsync).WithName("DeleteSemanticLayerReportQuestion");
        reports.MapGet("/subscribers", ListSubscribersAsync).WithName("ListSemanticLayerReportSubscribers");
        reports.MapGet(string.Empty, ListAsync).WithName("ListSemanticLayerReports");
        reports.MapGet("/{id:long}", GetAsync).WithName("GetSemanticLayerReport");
        reports.MapPost(string.Empty, StoreAsync)
            .WithName("StoreSemanticLayerReport")
            .WithMetadata(new RequestSizeLimitAttribute(MaxStoreRequestBytes));
        reports.MapPost("/extract", ExtractAsync)
            .WithName("ExtractSemanticLayerReport")
            .WithMetadata(new RequestSizeLimitAttribute(1024L * 1024L * 1024L));
        reports.MapDelete("/{id:long}", DeleteAsync).WithName("DeleteSemanticLayerReport");

        return group;
    }

    private static async Task<Ok<SemanticReportCapabilitiesDto>> GetCapabilities(
        ReportExtractionClient extraction, QuestionGenerationSwitch questionGeneration, CatalogDbContext db,
        CancellationToken ct)
        => TypedResults.Ok(new SemanticReportCapabilitiesDto(
            extraction.IsEnabled, extraction.MaxUploadBytes, ReportSpecs.MaxBytes,
            await questionGeneration.IsEnabledAsync(db, ct).ConfigureAwait(false)));

    private static async Task<Ok<QuestionGenerationDto>> GetQuestionGenerationAsync(
        QuestionGenerationSwitch questionGeneration, CatalogDbContext db, CancellationToken ct)
        => TypedResults.Ok(ToDto(await questionGeneration.ReadAsync(db, ct).ConfigureAwait(false), syncsQueued: 0));

    /// <summary>
    /// Turns sync-time question generation on or off, or back to the deployment default. Refused (409) when it would
    /// turn generation on where the deployment has no Anthropic key. A change that turns it on queues the managed
    /// sync of every repo holding report visuals, since the questions only appear when a sync generates them.
    /// </summary>
    private static async Task<Results<Ok<QuestionGenerationDto>, ProblemHttpResult>> SetQuestionGenerationAsync(
        SetQuestionGenerationRequest request, QuestionGenerationSwitch questionGeneration, CatalogDbContext db,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        var before = await questionGeneration.ReadAsync(db, ct).ConfigureAwait(false);
        var wouldEnable = request.Enabled ?? before.DeploymentDefault;
        if (wouldEnable && !before.Available)
        {
            return TypedResults.Problem(
                detail: "Question generation needs an Anthropic key, and this deployment has none. Set "
                    + "ControlPlane:Assistant:Anthropic:ApiKey and restart the control plane.",
                statusCode: StatusCodes.Status409Conflict, title: "Question generation unavailable");
        }

        var after = await questionGeneration.SetAsync(
            db, request.Enabled, SemanticLayer.Actor(user), clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);

        var queued = 0;
        if (after.Enabled && !before.Enabled)
        {
            var repoNames = await db.SubscriberReportVisuals.AsNoTracking()
                .Select(v => v.RepoId).Distinct()
                .Join(db.Repos.AsNoTracking(), id => id, r => r.Id, (_, r) => r.Name)
                .OrderBy(name => name)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var repoName in repoNames)
            {
                if (await RepoSourceEndpoints.QueueSyncForRepoAsync(
                        db, repoName, "Power BI question generation turned on", clock, ct).ConfigureAwait(false))
                {
                    queued++;
                }
            }
        }

        return TypedResults.Ok(ToDto(after, queued));
    }

    /// <summary>The longest question the catalog stores.</summary>
    private const int MaxQuestionLength = 400;

    /// <summary>
    /// Adds a person's question to a visual the last sync served. It is served to question search at once, and no
    /// sync replaces it; it goes only when the visual leaves the report.
    /// </summary>
    private static async Task<Results<Ok<SubscriberReportQuestionDto>, ProblemHttpResult>> AddQuestionAsync(
        AddReportQuestionRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        if (request.RepoId is not { } repoId || repoId == Guid.Empty)
        {
            return Invalid("repoId is required.");
        }

        if (SemanticLayer.TrimToNull(request.VisualKey) is not { } visualKey)
        {
            return Invalid("visualKey is required.");
        }

        if (QuestionProblem(request.Question, out var question) is { } problem)
        {
            return Invalid(problem);
        }

        var visualExists = await db.SubscriberReportVisuals.AsNoTracking()
            .AnyAsync(v => v.RepoId == repoId && v.VisualKey == visualKey, ct).ConfigureAwait(false);
        if (!visualExists)
        {
            return TypedResults.Problem(
                detail: $"No report visual '{visualKey}' in repo '{repoId}'. It may have left the report since the page "
                    + "was loaded; reload it.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var siblings = await db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => q.RepoId == repoId && q.VisualKey == visualKey)
            .Select(q => new { q.Ordinal, q.Question })
            .ToListAsync(ct).ConfigureAwait(false);
        if (siblings.Any(q => string.Equals(q.Question, question, StringComparison.OrdinalIgnoreCase)))
        {
            return Duplicate(question);
        }

        var row = new CatalogSubscriberReportVisualQuestion
        {
            RepoId = repoId,
            VisualKey = visualKey,
            Ordinal = siblings.Count == 0 ? 1 : siblings.Max(q => q.Ordinal) + 1,
            Question = question,
            Origin = SubscriberQuestionOrigin.Manual,
            UpdatedBy = SemanticLayer.Actor(user),
            UpdatedUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.SubscriberReportVisualQuestions.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(row));
    }

    /// <summary>Rewrites a question. A generated one becomes a person's, so a later sync keeps it as written.</summary>
    private static async Task<Results<Ok<SubscriberReportQuestionDto>, ProblemHttpResult>> UpdateQuestionAsync(
        long id, UpdateReportQuestionRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        if (QuestionProblem(request.Question, out var question) is { } problem)
        {
            return Invalid(problem);
        }

        var row = await db.SubscriberReportVisualQuestions.AsTracking()
            .FirstOrDefaultAsync(q => q.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return QuestionNotFound(id);
        }

        var others = await db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => q.RepoId == row.RepoId && q.VisualKey == row.VisualKey && q.Id != id)
            .Select(q => q.Question)
            .ToListAsync(ct).ConfigureAwait(false);
        if (others.Any(q => string.Equals(q, question, StringComparison.OrdinalIgnoreCase)))
        {
            return Duplicate(question);
        }

        row.Question = question;
        row.Origin = SubscriberQuestionOrigin.Manual;
        row.UpdatedBy = SemanticLayer.Actor(user);
        row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A sync regenerating this visual deleted the generated question between the read and the write.
            return TypedResults.Problem(
                detail: $"Question '{id}' was replaced by a sync while it was being edited. Reload and edit the new one.",
                statusCode: StatusCodes.Status409Conflict, title: "Question changed");
        }

        return TypedResults.Ok(ToDto(row));
    }

    /// <summary>
    /// Deletes a question, generated or a person's. A visual left with no questions at all gets freshly generated ones
    /// at the next sync while generation is on.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteQuestionAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var deleted = await db.SubscriberReportVisualQuestions
            .Where(q => q.Id == id)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted == 0 ? QuestionNotFound(id) : TypedResults.NoContent();
    }

    /// <summary>Null when <paramref name="value"/> is a storable question, with the trimmed text in
    /// <paramref name="question"/>; otherwise why it is not.</summary>
    private static string? QuestionProblem(string? value, out string question)
    {
        question = SemanticLayer.TrimToNull(value) ?? string.Empty;
        if (question.Length == 0)
        {
            return "question is required.";
        }

        if (question.Length > MaxQuestionLength)
        {
            return $"question is longer than {MaxQuestionLength} characters.";
        }

        return question.Any(char.IsControl) ? "question must be a single line of text." : null;
    }

    private static ProblemHttpResult Duplicate(string question)
        => TypedResults.Problem(
            detail: $"The visual already has the question '{question}'.",
            statusCode: StatusCodes.Status409Conflict, title: "Duplicate question");

    private static ProblemHttpResult QuestionNotFound(long id)
        => TypedResults.Problem(
            detail: $"No report question with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static SubscriberReportQuestionDto ToDto(CatalogSubscriberReportVisualQuestion row)
        => new(row.Id, row.Question, row.Origin, row.UpdatedBy, row.UpdatedUtc);

    private static QuestionGenerationDto ToDto(QuestionGenerationState state, int syncsQueued)
        => new(state.Available, state.DeploymentDefault, state.Override, state.Enabled, state.UpdatedBy,
            state.UpdatedUtc, syncsQueued);

    /// <summary>The Power BI subscribers the repositories declare, which are what a report can be attached to.</summary>
    private static async Task<Ok<IReadOnlyList<SemanticReportSubscriberDto>>> ListSubscribersAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.Subscribers.AsNoTracking()
            .Join(db.Repos.AsNoTracking(), s => s.RepoId, r => r.Id, (s, r) => new { s, RepoName = r.Name })
            .OrderBy(x => x.RepoName).ThenBy(x => x.s.Name)
            .Select(x => new { x.s.RepoId, x.RepoName, x.s.ObjectKey, x.s.Name, x.s.Type, x.s.Owner })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows
            .Where(r => IsPowerBi(r.Type))
            .Select(r => new SemanticReportSubscriberDto(r.RepoId, r.RepoName, r.ObjectKey, r.Name, r.Type, r.Owner))
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SemanticReportSubscriberDto>>(items);
    }

    /// <summary>Every stored report, optionally for one repo or one subscriber, uploads first.</summary>
    private static async Task<Ok<IReadOnlyList<SemanticReportSpecDto>>> ListAsync(
        CatalogDbContext db, Guid? repoId, string? subscriber, CancellationToken ct)
    {
        var query = db.SemanticReportSpecs.AsNoTracking();
        if (repoId is { } repo)
        {
            query = query.Where(s => s.RepoId == repo);
        }

        if (SemanticLayer.TrimToNull(subscriber) is { } named)
        {
            var key = SubscriberKeyOf(named);
            query = query.Where(s => s.SubscriberKey == key);
        }

        var rows = await query
            .OrderBy(s => s.Origin == ReportSpecOrigin.Upload ? 0 : 1)
            .ThenBy(s => s.SubscriberKey).ThenBy(s => s.ReportFile)
            .Select(s => new SpecRow(
                s.Id, s.RepoId, s.SubscriberKey, s.ReportFile, s.Origin, s.Pages, s.Visuals, s.Tables, s.Measures,
                s.UpdatedBy, s.UpdatedUtc))
            .ToListAsync(ct).ConfigureAwait(false);

        var context = await DescribeAsync(db, rows, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<SemanticReportSpecDto>>(rows.Select(context.ToDto).ToList());
    }

    private static async Task<Results<Ok<SemanticReportSpecDetailDto>, ProblemHttpResult>> GetAsync(
        long id, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.SemanticReportSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(id);
        }

        return TypedResults.Ok(await DetailAsync(db, row, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Stores a specification for a subscriber: validated and rewritten into the canonical, credential-redacted form
    /// first, so the store never holds anything a sync would refuse. Storing the same report again replaces the earlier
    /// upload. The repo's managed sync is then asked to apply it.
    /// </summary>
    private static async Task<Results<Ok<StoreSemanticReportSpecResult>, ProblemHttpResult>> StoreAsync(
        StoreSemanticReportSpecRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Invalid("A request body is required.");
        }

        if (request.RepoId is not { } repoId || repoId == Guid.Empty)
        {
            return Invalid("repoId is required.");
        }

        if (SemanticLayer.TrimToNull(request.Subscriber) is not { } subscriberName)
        {
            return Invalid("subscriber is required.");
        }

        if (ReportSpecs.ReportFileProblem(request.ReportFile) is { } labelProblem)
        {
            return Invalid(labelProblem);
        }

        if (string.IsNullOrWhiteSpace(request.Spec))
        {
            return Invalid("spec is required.");
        }

        var repoName = await db.Repos.AsNoTracking()
            .Where(r => r.Id == repoId).Select(r => r.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repoName is null)
        {
            return TypedResults.Problem(
                detail: $"No repo '{repoId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var subscriberKey = SubscriberKeyOf(subscriberName);
        var subscriber = await db.Subscribers.AsNoTracking()
            .Where(s => s.RepoId == repoId && s.ObjectKey == subscriberKey)
            .Select(s => new { s.Name, s.Type })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (subscriber is null)
        {
            return TypedResults.Problem(
                detail: $"Repo '{repoName}' declares no subscriber '{subscriberName}'. Declare it in the repository's "
                    + "subscribers.yaml (type: PowerBI, with the 'server' its report reads through) and sync first.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (!IsPowerBi(subscriber.Type))
        {
            return Invalid($"Subscriber '{subscriber.Name}' is a {subscriber.Type} subscriber; a Power BI report can only "
                + "be attached to one of type PowerBI.");
        }

        string spec;
        ReportSpecSummary summary;
        try
        {
            spec = ReportSpecs.Normalize(request.Spec, "the specification");
            summary = ReportSpecs.Inspect(spec, "the specification");
        }
        catch (PbixExtractException ex)
        {
            return TypedResults.Problem(
                detail: $"{ex.Message}. Produce it with 'sqlflow powerbi extract <report.pbix>'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Specification refused");
        }

        var reportFile = request.ReportFile!;
        var identity = SemanticReportSpecIdentity.Compute(repoId, subscriberKey, ReportSpecOrigin.Upload, reportFile);
        var now = clock.GetUtcNow().UtcDateTime;
        var replaced = await UpsertAsync(db, identity, row =>
        {
            row.RepoId = repoId;
            row.SubscriberKey = subscriberKey;
            row.ReportFile = reportFile;
            row.Origin = ReportSpecOrigin.Upload;
            row.IdentityHash = identity;
            row.Spec = spec;
            row.ContentHash = ReportSpecs.Hash(spec);
            row.Pages = summary.Pages;
            row.Visuals = summary.Visuals;
            row.Tables = summary.Tables;
            row.Measures = summary.Measures;
            row.UpdatedBy = SemanticLayer.Actor(user);
            row.UpdatedUtc = now;
        }, ct).ConfigureAwait(false);

        var queued = await RepoSourceEndpoints.QueueSyncForRepoAsync(
            db, repoName, $"Power BI report '{reportFile}' uploaded", clock, ct).ConfigureAwait(false);

        var stored = await db.SemanticReportSpecs.AsNoTracking()
            .FirstAsync(s => s.IdentityHash == identity, ct).ConfigureAwait(false);
        return TypedResults.Ok(new StoreSemanticReportSpecResult(
            await DetailAsync(db, stored, ct).ConfigureAwait(false), replaced, queued));
    }

    /// <summary>
    /// Extracts an uploaded <c>.pbix</c> in the isolated extractor and answers with its specification, without storing
    /// it: the caller reviews what was read and stores it through <see cref="StoreAsync"/>. The body is the raw report
    /// (<c>application/octet-stream</c>); it is streamed to the extractor and never parsed or kept here.
    /// </summary>
    private static async Task<Results<Ok<ExtractedSemanticReportDto>, ProblemHttpResult>> ExtractAsync(
        HttpContext http, string? reportFile, ReportExtractionClient extraction, CancellationToken ct)
    {
        if (!extraction.IsEnabled)
        {
            return TypedResults.Problem(
                detail: "Report extraction is not enabled on this control plane (ControlPlane:PowerAI:ReportExtraction). "
                    + "Upload a specification made with 'sqlflow powerbi extract' instead.",
                statusCode: StatusCodes.Status501NotImplemented, title: "Not enabled");
        }

        if (ReportSpecs.ReportFileProblem(reportFile) is { } labelProblem)
        {
            return Invalid(labelProblem);
        }

        if (http.Request.ContentLength > extraction.MaxUploadBytes)
        {
            return TooLarge(extraction);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = extraction.MaxUploadBytes;
        }

        ReportExtractionOutcome outcome;
        try
        {
            outcome = await extraction.ExtractAsync(http.Request.Body, reportFile!, ct).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(extraction);
        }

        if (outcome.Spec is null)
        {
            return TypedResults.Problem(
                detail: outcome.Problem, statusCode: outcome.StatusCode,
                title: outcome.StatusCode == StatusCodes.Status422UnprocessableEntity
                    ? "The report could not be extracted"
                    : "Extraction failed");
        }

        try
        {
            var spec = ReportSpecs.Normalize(outcome.Spec, "the extractor's answer");
            return TypedResults.Ok(new ExtractedSemanticReportDto(
                reportFile!, spec, ReportSpecs.Inspect(spec, "the extractor's answer")));
        }
        catch (PbixExtractException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Extraction failed");
        }
    }

    /// <summary>Deletes a stored report and asks the repo's managed sync to drop what it contributed. A sync's own copy
    /// of a declared report can be deleted too; the next sync that can extract that report stores it again.</summary>
    private static async Task<Results<Ok<DeleteSemanticReportSpecResult>, ProblemHttpResult>> DeleteAsync(
        long id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var row = await db.SemanticReportSpecs.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.RepoId, s.ReportFile })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(id);
        }

        var deleted = await db.SemanticReportSpecs.Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted == 0)
        {
            return NotFound(id);
        }

        var repoName = await db.Repos.AsNoTracking()
            .Where(r => r.Id == row.RepoId).Select(r => r.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var queued = repoName is not null && await RepoSourceEndpoints.QueueSyncForRepoAsync(
            db, repoName, $"Power BI report '{row.ReportFile}' removed", clock, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeleteSemanticReportSpecResult(queued));
    }

    /// <summary>Inserts or updates the row with <paramref name="identity"/>, returning whether one already existed. The
    /// unique identity index decides a race between two uploads of the same report: the loser updates the winner's
    /// row, since the later upload is the one a person meant to keep.</summary>
    private static async Task<bool> UpsertAsync(
        CatalogDbContext db, string identity, Action<CatalogSemanticReportSpec> apply, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.SemanticReportSpecs.AsTracking()
                .FirstOrDefaultAsync(s => s.IdentityHash == identity, ct).ConfigureAwait(false);
            var existed = row is not null;
            if (row is null)
            {
                row = new CatalogSemanticReportSpec();
                db.SemanticReportSpecs.Add(row);
            }

            apply(row);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return existed;
            }
            catch (DbUpdateException) when (attempt == 0 && !existed)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<SemanticReportSpecDetailDto> DetailAsync(
        CatalogDbContext db, CatalogSemanticReportSpec row, CancellationToken ct)
    {
        var spec = new SpecRow(
            row.Id, row.RepoId, row.SubscriberKey, row.ReportFile, row.Origin, row.Pages, row.Visuals, row.Tables,
            row.Measures, row.UpdatedBy, row.UpdatedUtc);
        var context = await DescribeAsync(db, [spec], ct).ConfigureAwait(false);
        var pages = await LineageEndpoints.LoadSubscriberReportPagesAsync(
            db, row.RepoId, row.SubscriberKey, row.ReportFile, ct).ConfigureAwait(false);

        // Stored specifications were validated on the way in, so reading one back cannot fail.
        return new SemanticReportSpecDetailDto(
            context.ToDto(spec), row.Spec, ReportSpecs.Inspect(row.Spec, $"report '{row.ReportFile}'"), pages);
    }

    /// <summary>The repo names and declared subscribers the given rows refer to, read in two queries.</summary>
    private static async Task<SpecContext> DescribeAsync(
        CatalogDbContext db, IReadOnlyList<SpecRow> rows, CancellationToken ct)
    {
        var repoIds = rows.Select(r => r.RepoId).Distinct().ToList();
        var repos = await db.Repos.AsNoTracking()
            .Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        var subscribers = await db.Subscribers.AsNoTracking()
            .Where(s => repoIds.Contains(s.RepoId))
            .Select(s => new { s.RepoId, s.ObjectKey, s.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var names = new Dictionary<(Guid, string), string>();
        foreach (var s in subscribers)
        {
            names.TryAdd((s.RepoId, s.ObjectKey), s.Name);
        }

        return new SpecContext(repos, names);
    }

    /// <summary>The subscriber key a name (or an already-formed key) stands for: the one node-identity rule.</summary>
    private static string SubscriberKeyOf(string subscriber)
        => subscriber.StartsWith(ServerIdentity.Subscriber + "|", StringComparison.Ordinal)
            ? subscriber
            : NodeKey.For(ServerIdentity.Subscriber, database: null, schema: null, subscriber);

    /// <summary>Whether a subscriber's free-text type names Power BI, however it is spaced or cased.</summary>
    private static bool IsPowerBi(string type)
        => string.Equals(type.Replace(" ", string.Empty, StringComparison.Ordinal), "PowerBI", StringComparison.OrdinalIgnoreCase);

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    private static ProblemHttpResult TooLarge(ReportExtractionClient extraction)
        => TypedResults.Problem(
            detail: $"The report is larger than {extraction.MaxUploadBytes / (1024 * 1024)} MB.",
            statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large");

    private static ProblemHttpResult NotFound(long id)
        => TypedResults.Problem(
            detail: $"No stored report with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private sealed record SpecRow(
        long Id, Guid RepoId, string SubscriberKey, string ReportFile, string Origin, int Pages, int Visuals,
        int Tables, int Measures, string? UpdatedBy, DateTime UpdatedUtc);

    private sealed record SpecContext(
        IReadOnlyDictionary<Guid, string> Repos, IReadOnlyDictionary<(Guid, string), string> Subscribers)
    {
        public SemanticReportSpecDto ToDto(SpecRow row)
        {
            var declared = Subscribers.TryGetValue((row.RepoId, row.SubscriberKey), out var name);
            return new SemanticReportSpecDto(
                row.Id, row.RepoId, Repos.TryGetValue(row.RepoId, out var repo) ? repo : row.RepoId.ToString(),
                row.SubscriberKey, name ?? DisplayNameOf(row.SubscriberKey), declared, row.ReportFile, row.Origin,
                row.Pages, row.Visuals, row.Tables, row.Measures, row.UpdatedBy, row.UpdatedUtc);
        }

        /// <summary>The name part of a subscriber key, for a subscriber the repository no longer declares.</summary>
        private static string DisplayNameOf(string key)
        {
            var at = key.LastIndexOf('|');
            return at >= 0 ? key[(at + 1)..] : key;
        }
    }
}
