using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The body that triggers a run: references only (the repo, the flow name, an optional target pool, and an
/// optional commit SHA). A secret is never accepted here; the worker resolves every credential from the executing
/// host's own environment. <c>pool</c> routes the run to a node serving that pool (omit for any node);
/// <c>commitSha</c> pins the run to an exact git version the node materializes. Omitting it pins the run to the
/// repo's last synced commit (so any node can execute it, and the executed version always matches what the catalog
/// shows); only a repo with no resolvable synced commit runs unpinned from the node's local copy.
/// <para>The built-in backfill lives here as per-run substitution parameters, all optional and all audited on the
/// run: <c>fullLoad</c> ignores the watermark and reads everything the definition selects; <c>backfillFrom</c> /
/// <c>backfillTo</c> is an externally-bounded window (file dates for file flows, the incremental date column for
/// ingestion flows, the chunk plan for exports and InitLoads); <c>filePattern</c> narrows a file flow to one glob
/// for this run. <c>assertionsOnly</c> (ingestion flows only) evaluates the flow's data-quality assertions,
/// manual-mode ones included, against the current target without loading anything. None of them touches the
/// definition in git.</para></summary>
public sealed record RunTriggerRequest(
    Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null,
    bool FullLoad = false, DateTime? BackfillFrom = null, DateTime? BackfillTo = null, string? FilePattern = null,
    string? Scope = null, string? Batch = null, bool AssertionsOnly = false, string? SourceFilter = null,
    bool IncludeAll = false);

/// <summary>The accepted-run acknowledgement: the minted run id and its queued status. The run executes
/// asynchronously; poll <c>GET /api/v1/runs/{runId}</c> (the <c>Location</c> header) for the outcome.</summary>
public sealed record RunTriggerAccepted(Guid RunId, string Status);

/// <summary>The accepted-group acknowledgement for a multi-flow run (Node / Batch): the minted group id, how many
/// member flows were queued, and the status. Poll <c>GET /api/v1/runs/groups/{groupId}</c> (the <c>Location</c>
/// header) for the group's live state.</summary>
public sealed record RunGroupAccepted(Guid GroupId, int MemberCount, string Status);

/// <summary>
/// The run-trigger surface: <c>POST /api/v1/runs</c>. It validates the request (a non-blank flow name, and an
/// active pipeline in the catalog) and enqueues the run through the <see cref="IRunDispatcher"/>, returning
/// <c>202 Accepted</c> with the new run id and a <c>Location</c> pointing at the existing run-detail endpoint.
/// Triggering a run is privileged, so this is mapped under the "operate" scope (never the read group). The
/// contract is references-only: no secret is accepted in the request or echoed in the response.
/// </summary>
public static class RunTriggerEndpoints
{
    public static RouteGroupBuilder MapRunTriggerEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/runs", TriggerRunAsync)
            .WithTags("Runs")
            .WithName("TriggerRun")
            .RequireAuthorization("operate");

        group.MapPost("/runs/{runId:guid}/cancel", CancelRunAsync)
            .WithTags("Runs")
            .WithName("CancelRun")
            .RequireAuthorization("operate");

        group.MapPost("/runs/groups/{groupId:guid}/cancel", CancelGroupAsync)
            .WithTags("Runs")
            .WithName("CancelRunGroup")
            .RequireAuthorization("operate");

        return group;
    }

    private static async Task<Results<Accepted<RunTriggerAccepted>, Accepted<RunGroupAccepted>, ProblemHttpResult>> TriggerRunAsync(
        RunTriggerRequest request, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                detail: "A run trigger requires a request body.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        // The scope selects Flow (one flow) or Node (a flow and its descendants). There is no ad-hoc batch scope:
        // a whole source runs through its schedule (fire it, or POST /schedules/{id}/run), whose member set is
        // the single authority on what a source executes.
        var scope = RunScopeExpander.TryParseScope(request.Scope);
        if (scope is null)
        {
            return TypedResults.Problem(
                detail: "scope must be one of 'flow' or 'node' (or omitted for a single flow). To run a whole "
                        + "source, fire its schedule (POST /schedules/{id}/run): membership is what a fire runs.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        // An explicit pin must at least look like a git object id; catching garbage here (the trust boundary)
        // beats queueing a run every node is guaranteed to fail materializing.
        if (request.CommitSha is not null && !IsPlausibleCommitSha(request.CommitSha.Trim()))
        {
            return TypedResults.Problem(
                detail: "commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        return scope.Value == RunScope.Flow
            ? await TriggerSingleFlowAsync(request, db, dispatcher, ct).ConfigureAwait(false)
            : await TriggerGroupAsync(request, scope.Value, db, dispatcher, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Accepted<RunTriggerAccepted>, Accepted<RunGroupAccepted>, ProblemHttpResult>> TriggerSingleFlowAsync(
        RunTriggerRequest request, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FlowName))
        {
            return TypedResults.Problem(
                detail: "A run trigger requires a non-blank flowName.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        // The substitution parameters are validated at this trust boundary, so a run no engine path could honor
        // (an inverted window, a control character in a glob) is refused before it is ever queued. The built-in
        // backfill is a single-flow concept, so it lives only on this path (a group always runs default parameters).
        var parameters = new RunParameters
        {
            FullLoad = request.FullLoad,
            BackfillFrom = request.BackfillFrom,
            BackfillTo = request.BackfillTo,
            FilePattern = string.IsNullOrWhiteSpace(request.FilePattern) ? null : request.FilePattern.Trim(),
            AssertionsOnly = request.AssertionsOnly,
            SourceFilter = string.IsNullOrWhiteSpace(request.SourceFilter) ? null : request.SourceFilter.Trim(),
        };
        try
        {
            parameters.Validate();
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid run parameters");
        }

        var flowName = request.FlowName.Trim();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, flowName);

        // The pipeline must exist AND be active in the catalog: an unknown or soft-deactivated flow is a 404, so a
        // run is never queued for something the estate no longer exposes. The kind is carried onto the queued run.
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.RepoId == request.RepoId)
            .Select(p => new { p.Active, p.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is not { Active: true })
        {
            return TypedResults.Problem(
                detail: $"No active pipeline '{flowName}' in repo '{request.RepoId}'.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found");
        }

        // Assertions are an ingestion concept (they evaluate against the ingestion target); refusing here keeps a
        // run that no engine path could honor out of the queue, exactly like the parameter validation above.
        if (parameters.AssertionsOnly && !string.Equals(pipeline.Kind, "ing", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"assertionsOnly applies only to ingestion flows; '{flowName}' is a '{pipeline.Kind}' flow.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid run parameters");
        }

        var runId = await dispatcher.EnqueueAsync(
            db, new RunEnqueueRequest(request.RepoId, flowName, pipeline.Kind, request.Pool, request.CommitSha, parameters), ct).ConfigureAwait(false);

        // 202 with the canonical run-detail location: GET /api/v1/runs/{runId} reflects the run from the moment it
        // is queued (status "queued"), through running, to its terminal state.
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new RunTriggerAccepted(runId, "queued"));
    }

    private static async Task<Results<Accepted<RunTriggerAccepted>, Accepted<RunGroupAccepted>, ProblemHttpResult>> TriggerGroupAsync(
        RunTriggerRequest request, RunScope scope, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        // An assertions-only execution is a single-flow concept (like the built-in backfill); refusing beats
        // silently load-running a whole group the caller asked to only assert on.
        if (request.AssertionsOnly)
        {
            return TypedResults.Problem(
                detail: "assertionsOnly applies to a single ingestion flow; a node/batch scope always runs its members normally.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid run parameters");
        }

        // Node needs an anchor flow to expand descendants from.
        var anchorFlow = string.IsNullOrWhiteSpace(request.FlowName) ? null : request.FlowName.Trim();
        if (scope == RunScope.Node && anchorFlow is null)
        {
            return TypedResults.Problem(
                detail: "A node-scoped run requires a non-blank flowName to expand descendants from.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        RunScopeExpansion expansion;
        try
        {
            // includeAll ("find all") widens a Node expansion to its manual and disabled descendants for a
            // deliberate full replay; the default runs only the active (mode: auto) ones.
            expansion = await RunScopeExpander
                .ExpandAsync(db, request.RepoId, anchorFlow, scope, request.IncludeAll, ct)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (expansion.Members.Count == 0)
        {
            var what = scope == RunScope.Node
                ? $"flow '{anchorFlow}' (it is not an active pipeline in this repo)"
                : $"batch '{expansion.Anchor}' (no active flows)";
            return TypedResults.Problem(
                detail: $"Nothing to run for {what}.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found");
        }

        // A node backfill scopes the WINDOW to the anchor only (the flow the operator picked): a from/to window is a
        // modified-date selection at the source, which cannot be re-enforced downstream (re-copying a file stamps it
        // with today's timestamp, never a past one), so the anchor is the single place it applies. Its descendants
        // then just reprocess whatever the anchor re-lands: a relational descendant takes MIN-from-source (its rows
        // carry old business dates that MAX-from-target would filter out), while a file/copy descendant takes default
        // parameters and picks up the freshly re-landed files through its own normal incremental (their fresh
        // timestamps beat its watermark). A member left out of the map runs with defaults. The window is validated
        // here, at the trust boundary. A batch/schedule fire never reaches here (it runs its members as defined).
        Dictionary<string, RunParameters>? memberParameters = null;
        if (scope == RunScope.Node && (request.BackfillFrom is not null || request.BackfillTo is not null))
        {
            var window = new RunParameters { BackfillFrom = request.BackfillFrom, BackfillTo = request.BackfillTo };
            try
            {
                window.Validate();
            }
            catch (SqlFlowException ex)
            {
                return TypedResults.Problem(
                    detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid run parameters");
            }

            var reprocess = new RunParameters { ReprocessFromSourceMin = true };
            memberParameters = new Dictionary<string, RunParameters>(StringComparer.Ordinal);
            foreach (var member in expansion.Members)
            {
                if (string.Equals(member.FlowName, expansion.Anchor, StringComparison.Ordinal))
                {
                    memberParameters[member.FlowName] = window;
                }
                else if (string.Equals(member.FlowKind, "ing", StringComparison.OrdinalIgnoreCase))
                {
                    // Only a relational (silver) descendant re-pulls from the source minimum; that override affects
                    // no other kind. A file/copy descendant is left at defaults (it catches the re-landed files
                    // through its own incremental), and a kind with no backfill role (sp, hc, exp, ...) runs as defined.
                    memberParameters[member.FlowName] = reprocess;
                }
            }
        }

        var mode = scope == RunScope.Node ? RunGroupModes.Node : RunGroupModes.Batch;
        var result = await dispatcher.EnqueueGroupAsync(
            db,
            new RunGroupEnqueueRequest(
                request.RepoId, mode, expansion.Anchor, expansion.Members, request.Pool, request.CommitSha,
                memberParameters),
            ct).ConfigureAwait(false);

        // 202 with the group location: GET /api/v1/runs/groups/{groupId} reflects the whole set as it executes.
        return TypedResults.Accepted(
            $"/api/v1/runs/groups/{result.GroupId}",
            new RunGroupAccepted(result.GroupId, expansion.Members.Count, "queued"));
    }

    private static bool IsPlausibleCommitSha(string sha)
        => sha.Length is >= 4 and <= 64 && sha.All(char.IsAsciiHexDigit);

    private static async Task<Results<Ok<RunGroupAccepted>, Accepted<RunGroupAccepted>, ProblemHttpResult>> CancelGroupAsync(
        Guid groupId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var result = await dispatcher.CancelGroupAsync(db, groupId, ct).ConfigureAwait(false);
        if (!result.Found)
        {
            return TypedResults.Problem(
                detail: $"No run group '{groupId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var affected = result.CancelledQueued + result.RequestedRunning;
        // A running member's cancel is asynchronous (its node aborts the in-flight statement), so report "cancelling"
        // and 202 when any member is still running; otherwise every member was queued and is now cancelled outright.
        return result.RequestedRunning > 0
            ? TypedResults.Accepted(
                $"/api/v1/runs/groups/{groupId}", new RunGroupAccepted(groupId, affected, "cancelling"))
            : TypedResults.Ok(new RunGroupAccepted(groupId, affected, "cancelled"));
    }

    private static async Task<Results<Ok<RunTriggerAccepted>, Accepted<RunTriggerAccepted>, ProblemHttpResult>> CancelRunAsync(
        Guid runId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var outcome = await dispatcher.CancelAsync(db, runId, ct).ConfigureAwait(false);
        return outcome switch
        {
            // A still-queued run is cancelled synchronously (it never ran): 200 with the terminal status.
            CancelOutcome.Cancelled => TypedResults.Ok(new RunTriggerAccepted(runId, "cancelled")),
            // A running run's cancel is asynchronous: the owning node aborts the in-flight statement and records the
            // run cancelled. 202 with a transitional status; poll GET /api/v1/runs/{runId} for the terminal outcome.
            CancelOutcome.CancelRequested => TypedResults.Accepted(
                $"/api/v1/runs/{runId}", new RunTriggerAccepted(runId, "cancelling")),
            CancelOutcome.NotFound => TypedResults.Problem(
                detail: $"No run '{runId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found"),
            // A run that has already finished has nothing to cancel.
            _ => TypedResults.Problem(
                detail: $"Run '{runId}' has already finished and cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
        };
    }
}
