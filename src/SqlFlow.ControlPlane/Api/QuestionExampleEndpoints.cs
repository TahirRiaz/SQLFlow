using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// What a person decided about a proposed answer. POWERAI.md Section 6's loop is accept/correct/reject, and all
/// three arrive here: the first two are knowledge worth keeping, and the third is accepted and stores nothing.
/// </summary>
public static class QuestionConfirmationOutcome
{
    /// <summary>The proposed query answered the question as written.</summary>
    public const string Accepted = "accepted";

    /// <summary>The query was edited before it answered the question. What is stored is the CORRECTED query, so
    /// the example the estate learns from is the one that actually worked.</summary>
    public const string Corrected = "corrected";

    /// <summary>The query did not answer the question. Nothing is stored: POWERAI.md Section 6 is explicit that
    /// only correct, verified answers become precedent, so a rejection leaves no trace in the store.</summary>
    public const string Rejected = "rejected";

    /// <summary>Whether <paramref name="value"/> is one of the three outcomes, case-insensitively.</summary>
    public static bool IsKnown(string? value)
        => string.Equals(value, Accepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Corrected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Rejected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One confirmation of a question/query pair: what was asked, the query that was judged, what the person
/// decided, and the retrieval score the proposal was built from.
/// </summary>
/// <param name="Question">The question as it was asked.</param>
/// <param name="Sql">The query being confirmed. On <see cref="QuestionConfirmationOutcome.Corrected"/> this is
/// the corrected text, not the original proposal: the store holds what worked, never what was fixed.</param>
/// <param name="Outcome">One of <see cref="QuestionConfirmationOutcome"/>'s values.</param>
/// <param name="ObjectKeys">The warehouse objects the query reads, when the caller resolved them. Optional: an
/// example is still worth keeping without them, and inventing keys that no lineage pass produced would put
/// unverified identities into the catalog.</param>
/// <param name="Confidence">The retrieval score the confirmed answer was built from, or null when it was
/// composed without a prior match. Never a model's own estimate of its correctness.</param>
/// <param name="RepoId">The repo to attribute the example to, or null to keep it estate-wide.</param>
/// <param name="SourceRef">The datasource <paramref name="Sql"/> runs against: a whole <c>${env:...}</c>/
/// <c>${keyvault:...}</c> reference or an <c>@alias</c>, matching a datasource the catalog already declares
/// (the same shape and the same known-reference gate <c>PrepareQueryRequest.Reference</c> enforces). Optional:
/// omitted, it is worked out from the objects the query reads (<see cref="DatasourceInference"/>), and the
/// example is stored without one only when those objects do not point at exactly one declared datasource. Ignored on <see cref="QuestionConfirmationOutcome.Rejected"/>,
/// which is never stored at all.</param>
public sealed record ConfirmQuestionRequest(
    string? Question, string? Sql, string? Outcome, IReadOnlyList<string>? ObjectKeys = null,
    int? Confidence = null, Guid? RepoId = null, string? SourceRef = null);

/// <summary>
/// What the confirmation did. <see cref="Stored"/> is the fact that matters: a rejection is accepted by the
/// endpoint and stores nothing, so a caller must read this rather than infer from the 200 that the estate
/// learned something.
/// </summary>
/// <param name="Stored">Whether an example was written or refreshed.</param>
/// <param name="ExampleId">The row that now holds the example, or null when nothing was stored.</param>
/// <param name="Outcome">The outcome as it was recorded, normalized to lower case.</param>
/// <param name="Provenance">The provenance the stored example carries, or null when nothing was stored.</param>
/// <param name="Message">What happened, in a sentence a caller can show a person verbatim.</param>
public sealed record ConfirmedQuestionDto(
    bool Stored, long? ExampleId, string Outcome, string? Provenance, string Message);

/// <summary>
/// What auto-running a trusted match did. <see cref="Ran"/> is the fact that matters: true means
/// <see cref="Result"/> holds the capped rows the confirmed example's SQL actually returned; false means the
/// budget was exceeded (or the query failed on the live source) and nothing was returned, so the caller must
/// fall back to <c>prepare_query</c>/<c>run_query</c> and a person's confirmation, exactly as before this
/// endpoint existed. <see cref="Sql"/> and <see cref="SourceRef"/> travel on every response, ran or not, so a
/// caller falling back has everything <c>prepare_query</c> needs without a second lookup.
/// </summary>
/// <param name="Ran">Whether the query actually completed within the auto-run budget.</param>
/// <param name="ExampleId">The confirmed example that was run.</param>
/// <param name="Question">The question this example answers.</param>
/// <param name="Sql">The SQL that was (or would have been) run.</param>
/// <param name="SourceRef">The datasource it ran (or would run) against.</param>
/// <param name="MaxRows">The row cap this run was enqueued with, always the deployment's auto-run cap.</param>
/// <param name="TimeoutSeconds">The command timeout this run was enqueued with, always the deployment's
/// auto-run cap.</param>
/// <param name="TaskId">The compute task this run's result is recorded on, for the task-history view.</param>
/// <param name="Status">The task's terminal status when it reached one within the budget; the status it was
/// left in (and then cancelled from) when the budget was exceeded.</param>
/// <param name="Result">The query's own result document (columns/rows/truncated), present only when
/// <see cref="Ran"/> is true.</param>
/// <param name="Message">What happened, in a sentence a caller can show a person verbatim.</param>
public sealed record AutoRunResultDto(
    bool Ran, long ExampleId, string Question, string Sql, string SourceRef, int MaxRows, int TimeoutSeconds,
    Guid? TaskId, string? Status, JsonElement? Result, string Message);

/// <summary>
/// The write half of PowerAI retrieval: the confirmed-example store POWERAI.md Sections 6 and 8 specify, and
/// the endpoint that grows it from real usage. Retrieval without this reads only what PowerBI extraction
/// derived, so the system never gets better at the questions people actually ask; with it, an answer a person
/// checked today is precedent the next similar question is matched against.
/// <para>
/// It lives under the "operate" scope rather than the read group because it WRITES estate knowledge that later
/// answers are grounded in. A caller who can add confirmed examples can steer every future answer toward a
/// query of their choosing, which is a privilege, not a read.
/// </para>
/// <para>
/// It is deliberately NOT a second execution path: nothing here runs a query or reaches a datasource. The
/// statement is parsed to prove it is read-only (the same <see cref="ReadOnlyQueryGuard"/> the DataOps prepare
/// step uses, so the store cannot come to hold a query that would write if anyone ran it), then stored as text.
/// Running it still goes through prepare/run and their human gate, unchanged.
/// </para>
/// </summary>
public static class QuestionExampleEndpoints
{
    public static RouteGroupBuilder MapQuestionExampleEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/powerai/questions/confirm", ConfirmAsync)
            .WithTags("PowerAI").WithName("ConfirmQuestionExample");
        group.MapPost("/powerai/questions/{exampleId:long}/auto-run", AutoRunAsync)
            .WithTags("PowerAI").WithName("AutoRunQuestionExample");

        return group;
    }

    /// <summary>
    /// Records what a person decided about a question/query pair, storing the pair as a confirmed example when
    /// they accepted or corrected it. Confirming the same pair again refreshes the existing row rather than
    /// adding a duplicate, so reaffirming an answer does not give it extra weight in every later search.
    /// </summary>
    private static async Task<Results<Ok<ConfirmedQuestionDto>, ProblemHttpResult>> ConfirmAsync(
        ConfirmQuestionRequest request, CatalogDbContext db, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (request is null)
        {
            return Problem("A confirmation requires a request body.", StatusCodes.Status400BadRequest);
        }

        // The same gate the search side answers 501 on. The store exists to be searched, so a deployment that
        // has not enabled retrieval has not opted into this feature at all, and silently accumulating
        // confirmations nobody can ever retrieve would be worse than saying so.
        if (!options.Value.PowerAI.Retrieval.Enabled)
        {
            return Problem(
                "Question retrieval is not enabled on this deployment "
                + "(ControlPlane:PowerAI:Retrieval:Enabled), so there is no example store to confirm into.",
                StatusCodes.Status501NotImplemented, "Retrieval not configured");
        }

        if (!QuestionConfirmationOutcome.IsKnown(request.Outcome))
        {
            return Problem(
                $"Unknown outcome '{request.Outcome}'. Valid values: "
                + $"{QuestionConfirmationOutcome.Accepted}, {QuestionConfirmationOutcome.Corrected}, "
                + $"{QuestionConfirmationOutcome.Rejected}.",
                StatusCodes.Status400BadRequest);
        }

        var outcome = request.Outcome!.Trim().ToLowerInvariant();
        var question = NormalizeQuestion(request.Question);
        if (QuestionProblem(question) is { } questionProblem)
        {
            return Problem(questionProblem, StatusCodes.Status400BadRequest);
        }

        // A rejection is answered before the SQL is validated: nothing is stored either way, so refusing it because
        // the wrong query also failed to parse would only turn a person's "no" into an error. POWERAI.md Section 6
        // is explicit that only correct, verified answers become precedent: a rejected query is not knowledge the
        // estate keeps, so it is neither stored nor remembered as a "do not propose this again" row.
        if (string.Equals(outcome, QuestionConfirmationOutcome.Rejected, StringComparison.Ordinal))
        {
            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: false, ExampleId: null, outcome, Provenance: null,
                "Nothing was stored: only accepted and corrected answers are kept, so a refuted query never "
                + "becomes an example later answers are grounded in."));
        }

        string sql;
        try
        {
            sql = await ValidateSqlAsync(db, request.Sql, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Query refused");
        }

        if (request.RepoId is { } repoId)
        {
            var exists = await db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId, ct).ConfigureAwait(false);
            if (!exists)
            {
                return Problem($"No repo '{repoId}' exists in the catalog.", StatusCodes.Status404NotFound);
            }
        }

        // A good example carries a datasource only when the caller supplied it, since a confirmation made
        // before a datasource was chosen (or one confirmed from a PowerBI-derived question, which names a
        // model entity rather than a connection) is still worth storing without it.
        var sourceRef = SemanticLayer.TrimToNull(request.SourceRef);
        if (sourceRef is not null
            && await SourceRefProblemAsync(db, sourceRef, ct).ConfigureAwait(false) is { } sourceRefProblem)
        {
            return sourceRefProblem;
        }

        // The objects the query reads: the caller's when it resolved them, otherwise the catalogued tables the SQL
        // names, so an example confirmed without keys (every GUI confirmation) is still tied to the tables it reads
        // and served as one of their examples.
        var objectKeys = request.ObjectKeys is { Count: > 0 }
            ? request.ObjectKeys
            : await DatasourceInference.ObjectKeysFromSqlAsync(db, sql, ct).ConfigureAwait(false);

        // Named by nobody: the objects the query reads decide it, so an example is runnable later without a
        // person picking a connection the catalog already knows. An inferred value never replaces one a person
        // chose on an earlier confirmation of the same example.
        var sourceRefInferred = false;
        if (sourceRef is null)
        {
            sourceRef = (await DatasourceInference.InferAsync(db, sql, objectKeys, ct).ConfigureAwait(false))
                .Reference;
            sourceRefInferred = sourceRef is not null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var confirmedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        var hash = SemanticExampleHash.Compute(question, sql);

        // Tracked explicitly: the context defaults every query to QueryTrackingBehavior.NoTracking
        // (Program.cs), so without this the row below comes back Detached and every mutation made to it in
        // this branch (ConfirmedUtc, Confidence, ObjectKeys, RepoId, SourceRef) would be silently discarded by
        // SaveChangesAsync, with no error, no exception, and a 200 response that lied about having refreshed
        // anything.
        var existing = await db.SemanticExamples
            .AsTracking()
            .FirstOrDefaultAsync(e => e.ContentHash == hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Reaffirming an answer updates who last stood behind it and when, and takes the newer confidence
            // when one was supplied. The question and SQL are not rewritten: they are what the hash is OF, so
            // a change to either is a different example rather than an edit to this one.
            existing.ConfirmedUtc = now;
            existing.ConfirmedBy = confirmedBy;
            existing.Provenance = SemanticExampleProvenance.UserConfirmed;
            existing.Confidence = request.Confidence ?? existing.Confidence;
            if (request.ObjectKeys is { Count: > 0 } || (existing.ObjectKeys.Length == 0 && objectKeys.Count > 0))
            {
                existing.ObjectKeys = JoinObjectKeys(objectKeys);
            }

            if (sourceRef is not null && (!sourceRefInferred || string.IsNullOrWhiteSpace(existing.SourceRef)))
            {
                existing.SourceRef = sourceRef;
            }

            if (request.RepoId is not null)
            {
                existing.RepoId = request.RepoId;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: true, existing.Id, outcome, existing.Provenance,
                "This question and query were already a confirmed example; the existing one was refreshed "
                + "rather than stored twice, so reaffirming it does not give it extra weight in later searches."));
        }

        var example = new CatalogSemanticExample
        {
            RepoId = request.RepoId,
            Question = question,
            Sql = sql,
            ObjectKeys = JoinObjectKeys(objectKeys),
            Provenance = SemanticExampleProvenance.UserConfirmed,
            Confidence = request.Confidence,
            ConfirmedUtc = now,
            ConfirmedBy = confirmedBy,
            ContentHash = hash,
            SourceRef = sourceRef,
        };

        db.SemanticExamples.Add(example);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two people confirming the same answer at once: the unique index on the hash is what decides, and
            // the loser reports the winner's row rather than failing a confirmation that did land. Any other
            // write failure is rethrown, so a real problem is not disguised as a successful confirmation.
            db.Entry(example).State = EntityState.Detached;
            var winner = await db.SemanticExamples.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ContentHash == hash, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return TypedResults.Ok(new ConfirmedQuestionDto(
                Stored: true, winner.Id, outcome, winner.Provenance,
                "This question and query were confirmed concurrently by someone else; that example is the one "
                + "now stored."));
        }

        return TypedResults.Ok(new ConfirmedQuestionDto(
            Stored: true, example.Id, outcome, example.Provenance,
            "Stored as a confirmed example. Questions that mean the same thing will now find this query, and "
            + "the match will carry that a person confirmed it."));
    }

    /// <summary>
    /// Runs a TRUSTED confirmed example's SQL directly, capped small (POWERAI.md Section 6): no prepare/approve
    /// round trip, because the SQL was already shown to and confirmed by a person once, when it was stored. The
    /// row cap and command timeout are always the deployment's own auto-run limits, never a caller's, since the
    /// whole safety argument for skipping a fresh confirmation is that nobody watching THIS call chose those
    /// numbers. The endpoint then waits, capped at that same timeout plus a small fixed claim-latency allowance,
    /// for the task to finish: inside the budget, the capped rows come back inline; past it, the task is
    /// cancelled and the caller is told to fall back to prepare_query/run_query, exactly as if auto-run did not
    /// exist. This is the only DataOps entry point that runs a statement with no token a person approved in the
    /// moment; everything else it touches (the read-only parse, the enqueue, the executor) is the same shared
    /// path prepare/run uses, so it can never reach a connection or a statement shape those do not already allow.
    /// </summary>
    private static async Task<Results<Ok<AutoRunResultDto>, ProblemHttpResult>> AutoRunAsync(
        long exampleId, CatalogDbContext db, IRunDispatcher dispatcher, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.DataOps.Enabled)
        {
            return Problem(
                "Auto-run executes a query, which is part of the data-operations surface, not enabled in this "
                + "deployment. Set ControlPlane__DataOps__Enabled=true to turn it on.",
                StatusCodes.Status403Forbidden, "Not enabled");
        }

        var retrieval = options.Value.PowerAI.Retrieval;
        if (!retrieval.Enabled || !retrieval.AutoRun.Enabled)
        {
            return Problem(
                "Auto-run is not enabled on this deployment "
                + "(ControlPlane:PowerAI:Retrieval:AutoRun:Enabled). Use prepare_query/run_query instead.",
                StatusCodes.Status501NotImplemented, "Not enabled");
        }

        var example = await db.SemanticExamples.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == exampleId, ct).ConfigureAwait(false);
        if (example is null)
        {
            return Problem($"No confirmed example '{exampleId}' exists.", StatusCodes.Status404NotFound);
        }

        // An example stored without a datasource still says where it runs through the objects it reads; only when
        // those do not point at exactly one declared datasource is there nothing to run it against.
        // The database comes from the same evidence: objects a schema registration flow registered run in the
        // database they were registered in, which the connection may not open on by default.
        var sourceRef = example.SourceRef;
        var objectKeys = example.ObjectKeys.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inferred = await DatasourceInference.InferAsync(db, example.Sql, objectKeys, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            sourceRef = inferred.Reference;
        }

        var database = string.Equals(sourceRef, inferred.Reference, StringComparison.OrdinalIgnoreCase)
            ? inferred.Database
            : null;

        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return Problem(
                "This confirmed example has no datasource attached, and the tables its SQL reads do not point at "
                + "exactly one datasource the estate declares, so there is nothing to run it against. Use "
                + "prepare_query/run_query with the datasource named.",
                StatusCodes.Status409Conflict, "No datasource");
        }

        string sql;
        try
        {
            // Re-parsed rather than trusted from storage: a defence-in-depth re-check of the same guard
            // confirm_question already ran, so a row edited directly in the database (never through the
            // confirm endpoint) still cannot reach the executor as anything but a proven single SELECT.
            sql = ReadOnlyQueryGuard.Validate(example.Sql);

            // Re-checked for the same reason: a column can be marked sensitive at any time AFTER an example
            // naming it was confirmed, and this is the moment that example would actually read live data.
            await ColumnPolicyGuard.EnsureAllowedAsync(db, sql, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Query refused");
        }

        var autoRun = retrieval.AutoRun;
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.RunQuery,
            SourceRef = sourceRef,
            Database = database,
            Sql = sql,
            MaxRows = autoRun.MaxRows,
            TimeoutSeconds = autoRun.TimeoutSeconds,
        };

        try
        {
            payload.Validate();
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Invalid compute task");
        }

        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(
                payload.Operation, payload.SourceRef, payload.ProviderKind?.ToString(), payload.ToJson(),
                TargetPool: null, RequestedBy: user.FindFirst("sub")?.Value ?? user.Identity?.Name),
            ct).ConfigureAwait(false);

        var budget = TimeSpan.FromSeconds(autoRun.TimeoutSeconds) + AutoRunOptions.ClaimLatencyAllowance;
        var deadline = clock.GetUtcNow() + budget;

        CatalogComputeTask? task;
        while (true)
        {
            task = await db.ComputeTasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TaskId == taskId, ct).ConfigureAwait(false);
            if (task is null || RunStatuses.IsTerminal(task.Status) || clock.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(AutoRunPollInterval, ct).ConfigureAwait(false);
        }

        if (task is not null && RunStatuses.IsTerminal(task.Status))
        {
            if (task.Status == RunStatuses.Succeeded)
            {
                var result = task.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(task.ResultJson);
                return TypedResults.Ok(new AutoRunResultDto(
                    Ran: true, example.Id, example.Question, sql, sourceRef, autoRun.MaxRows,
                    autoRun.TimeoutSeconds, taskId, task.Status, result,
                    "Ran the trusted match directly, capped to "
                    + $"{autoRun.MaxRows} rows and a {autoRun.TimeoutSeconds}s timeout, with no separate "
                    + "confirmation: this SQL was already confirmed by a person when it was stored."));
            }

            return TypedResults.Ok(new AutoRunResultDto(
                Ran: false, example.Id, example.Question, sql, sourceRef, autoRun.MaxRows,
                autoRun.TimeoutSeconds, taskId, task.Status, Result: null,
                $"Auto-run did not produce a result: the task ended '{task.Status}'"
                + (string.IsNullOrWhiteSpace(task.Error) ? "." : $" ({task.Error}).")
                + " Fall back to prepare_query/run_query so a person can see and decide on this query, since " +
                "the source may no longer match what was confirmed."));
        }

        // Still queued or running past the budget: cancelled rather than left to finish unattended, since a
        // query this slow is no longer the "trivial, already-proven lookup" auto-run exists for, and getting a
        // human decision on it (via prepare_query/run_query) is the right outcome, not a longer wait here.
        await dispatcher.CancelComputeTaskAsync(db, taskId, ct).ConfigureAwait(false);
        return TypedResults.Ok(new AutoRunResultDto(
            Ran: false, example.Id, example.Question, sql, sourceRef, autoRun.MaxRows,
            autoRun.TimeoutSeconds, taskId, "cancelled", Result: null,
            $"Auto-run did not finish within its {budget.TotalSeconds:0}s budget and was cancelled. Fall back "
            + "to prepare_query/run_query, which has no such budget, so a person can approve this query "
            + "running for as long as it needs."));
    }

    /// <summary>How often auto-run polls the compute task it just enqueued, matching the task-status endpoint's
    /// own poll interval.</summary>
    private static readonly TimeSpan AutoRunPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Joins the object keys the way the catalog stores them everywhere else (newline-separated, blanks
    /// dropped), so a confirmed example's keys read back exactly like a subscriber query's.</summary>
    internal static string JoinObjectKeys(IReadOnlyList<string>? objectKeys)
        => objectKeys is null
            ? string.Empty
            : string.Join('\n', objectKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()));

    /// <summary>A stored example's object keys as a list, the inverse of <see cref="JoinObjectKeys"/>.</summary>
    internal static IReadOnlyList<string> SplitObjectKeys(string objectKeys)
        => objectKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The longest question stored, matching the column's own length so an over-long question is
    /// refused with a stated reason rather than truncated into a different question.</summary>
    internal const int MaxQuestionLength = 1000;

    /// <summary>The chat command that routes a message down the business-question path ("!cwd what is our revenue").</summary>
    internal const string BusinessQuestionCommand = "!cwd";

    /// <summary>
    /// A question as it is stored: trimmed, and without a leading <see cref="BusinessQuestionCommand"/>. The GUI's
    /// confirmation row sends the message exactly as it was typed, while an assistant strips the command before
    /// searching, so without this the same question saved from two surfaces would be two questions, and the
    /// command's own word would count as a matched term in every later search.
    /// </summary>
    internal static string NormalizeQuestion(string? question)
    {
        var trimmed = question?.Trim() ?? string.Empty;
        var command = BusinessQuestionCommand.Length;
        if (trimmed.StartsWith(BusinessQuestionCommand, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == command || char.IsWhiteSpace(trimmed[command])))
        {
            return trimmed[command..].TrimStart();
        }

        return trimmed;
    }

    /// <summary>Why a trimmed question cannot be stored, or null when it can. Shared by confirming an example and
    /// editing one, so the two can never disagree about what a storable question is.</summary>
    internal static string? QuestionProblem(string question)
    {
        if (question.Length == 0)
        {
            return "The question must not be empty.";
        }

        return question.Length > MaxQuestionLength
            ? $"The question is {question.Length} characters, over the {MaxQuestionLength}-character limit."
            : null;
    }

    /// <summary>
    /// Proves <paramref name="sql"/> can be stored as an example and returns it trimmed, throwing the refusal
    /// otherwise. Parsed, not pattern-matched, for the same reason the prepare step parses: an example is a query
    /// later answers get adapted from, so one that would write if it ran must never enter the store, however it
    /// was labelled on the way in. The column allow-list is checked too, because an example is precedent for every
    /// future similar question, not only a query run once. Shared by confirming and editing.
    /// </summary>
    /// <exception cref="SqlFlowException">The statement is not a single read-only SELECT over allow-listed
    /// columns.</exception>
    internal static async Task<string> ValidateSqlAsync(CatalogDbContext db, string? sql, CancellationToken ct)
    {
        var validated = ReadOnlyQueryGuard.Validate(sql ?? string.Empty);
        await ColumnPolicyGuard.EnsureAllowedAsync(db, validated, ct).ConfigureAwait(false);
        return validated;
    }

    /// <summary>
    /// Why an explicitly chosen datasource cannot be attached to an example, or null when it can: it must be a whole
    /// <c>${env:...}</c>/<c>${keyvault:...}</c> reference or an <c>@alias</c>, and a reference must be one some
    /// pipeline declares. That is the same known-reference gate the DataOps prepare step enforces, so an auto-run
    /// can never be pointed at a connection the reviewed git estate never declared. Shared by confirming and editing.
    /// </summary>
    internal static async Task<ProblemHttpResult?> SourceRefProblemAsync(
        CatalogDbContext db, string sourceRef, CancellationToken ct)
    {
        if (!ComputeTaskPayload.IsWholeReference(sourceRef))
        {
            return Problem(
                "The datasource must be a whole ${env:...} / ${keyvault:...} reference or an @alias. "
                + "Inline connection strings are not accepted here.",
                StatusCodes.Status400BadRequest);
        }

        if (sourceRef.StartsWith('@'))
        {
            return null;
        }

        return await DatasourceInference.IsDeclaredAsync(db, sourceRef, ct).ConfigureAwait(false)
            ? null
            : Problem(
                $"No active pipeline in the catalog declares the datasource reference '{sourceRef}'.",
                StatusCodes.Status404NotFound);
    }

    private static ProblemHttpResult Problem(string detail, int statusCode, string title = "Invalid request")
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
