using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// The node protocol under <c>/api/v1/node</c>, the only way a compute node obtains, executes and reports work.
/// Every call is node-initiated and authenticated with a bearer credential carrying the <c>node</c> scope.
/// <c>POST /poll</c> is the heartbeat, lease renewal, cancel channel and hand-out in one long-polled call (each
/// hand-out carries its execution spec); <c>GET /flow-versions/{hash}</c> serves a run's snapshotted YAML;
/// <c>POST /runs/{id}/context</c> resolves the lineage facts a run depends on; <c>POST /runs/{id}/trace</c> takes
/// the live trace in batches; and <c>POST /runs/{id}/outcome</c> and <c>POST /tasks/{id}/outcome</c> report results,
/// every per-run call under the hand-out's fence. A replica whose dispatcher is not the owner answers 503 with a
/// retry hint (see <see cref="Infrastructure.GlobalExceptionHandler"/>), so a node behind a load balancer lands on
/// the owner within a retry or two.
/// </summary>
public static class NodeProtocolEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Room for the artifact plus the envelope around it.</summary>
    private const long OutcomeBodyLimit = NodeProtocol.MaxArtifactBytes + (4L * 1024 * 1024);

    /// <summary>The event levels a trace batch may carry, exactly the names the run event log uses.</summary>
    private static readonly HashSet<string> EventLevels = new(StringComparer.Ordinal)
    {
        RunEventLevels.Trace, RunEventLevels.Debug, RunEventLevels.Info, RunEventLevels.Warning, RunEventLevels.Error,
    };

    public static RouteGroupBuilder MapNodeProtocolEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/poll", PollAsync).WithTags("Node").WithName("NodePoll");
        group.MapGet("/flow-versions/{contentHash}", FlowVersionAsync).WithTags("Node").WithName("NodeFlowVersion");
        group.MapPost("/runs/{runId:guid}/context", RunContextAsync).WithTags("Node").WithName("NodeRunContext");
        group.MapPost("/runs/{runId:guid}/trace", RunTraceAsync).WithTags("Node").WithName("NodeRunTrace");
        group.MapPost("/runs/{runId:guid}/outcome", RunOutcomeAsync).WithTags("Node").WithName("NodeRunOutcome");
        group.MapPost("/tasks/{taskId:guid}/outcome", TaskOutcomeAsync).WithTags("Node").WithName("NodeTaskOutcome");
        return group;
    }

    private static async Task<Results<Ok<NodePollResponse>, ProblemHttpResult>> PollAsync(
        NodePollRequest request, Dispatcher dispatcher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Node))
        {
            return Invalid("node is required.");
        }

        if (request.RunSlots < 0 || request.FreeRunSlots < 0 || request.TaskSlots < 0 || request.FreeTaskSlots < 0)
        {
            return Invalid("slot counts must be zero or positive.");
        }

        if (request.WaitSeconds < 0 || request.WaitSeconds > NodeProtocol.MaxWaitSeconds)
        {
            return Invalid($"waitSeconds must be between 0 and {NodeProtocol.MaxWaitSeconds}.");
        }

        var response = await dispatcher.PollAsync(request, ct).ConfigureAwait(false);
        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<FlowVersionResponse>, ProblemHttpResult>> FlowVersionAsync(
        string contentHash, Dispatcher dispatcher, CancellationToken ct)
    {
        // A content hash is the SHA-256 hex the enqueue stamped; anything else cannot name a version.
        if (string.IsNullOrWhiteSpace(contentHash) || contentHash.Length > 64 || !contentHash.All(Uri.IsHexDigit))
        {
            return Invalid("contentHash must be the hexadecimal SHA-256 of a snapshotted flow version.");
        }

        var yaml = await dispatcher.LoadFlowVersionAsync(contentHash, ct).ConfigureAwait(false);
        if (yaml is null)
        {
            return TypedResults.Problem(
                detail: $"no flow version with content hash '{contentHash}' is staged.",
                statusCode: StatusCodes.Status404NotFound, title: "Flow version not found");
        }

        return TypedResults.Ok(new FlowVersionResponse(contentHash, yaml));
    }

    private static async Task<Results<Ok<RunContextResponse>, ProblemHttpResult>> RunContextAsync(
        Guid runId, RunContextRequest request, Dispatcher dispatcher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Node))
        {
            return Invalid("node is required.");
        }

        if (request.Attempt < 1)
        {
            return Invalid("attempt must be the value the hand-out carried (1 or more).");
        }

        if (string.IsNullOrWhiteSpace(request.TargetSchema) || string.IsNullOrWhiteSpace(request.TargetTable))
        {
            return Invalid("targetSchema and targetTable must name the flow's own target.");
        }

        var response = await dispatcher.ResolveRunContextAsync(runId, request, ct).ConfigureAwait(false);
        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<RunTraceResponse>, ProblemHttpResult>> RunTraceAsync(
        Guid runId, HttpContext context, Dispatcher dispatcher, CancellationToken ct)
    {
        var batch = await ReadBodyAsync<RunTraceBatch>(context, NodeProtocol.MaxTraceBatchBytes, ct).ConfigureAwait(false);
        if (batch is null)
        {
            return Invalid("the request body must be a trace batch document.");
        }

        if (string.IsNullOrWhiteSpace(batch.Node))
        {
            return Invalid("node is required.");
        }

        if (batch.Attempt < 1)
        {
            return Invalid("attempt must be the value the hand-out carried (1 or more).");
        }

        if (batch.Statements is null || batch.StatementFailures is null || batch.Events is null)
        {
            return Invalid("statements, statementFailures and events are required (empty lists when there is nothing of a kind).");
        }

        if (batch.Statements.Any(s => s is null || s.Ordinal < 1 || s.Step is null || s.Sql is null))
        {
            return Invalid("every statement needs an ordinal of 1 or more, a step and its SQL.");
        }

        if (batch.StatementFailures.Any(f => f is null || f.Ordinal < 1 || string.IsNullOrEmpty(f.Error)))
        {
            return Invalid("every statement failure needs an ordinal of 1 or more and the error.");
        }

        if (batch.Events.Any(e => e is null || e.Ordinal < 1 || e.Message is null || e.Level is null || !EventLevels.Contains(e.Level)))
        {
            return Invalid("every event needs an ordinal of 1 or more, a message and a level of trace, debug, info, warning or error.");
        }

        var accepted = await dispatcher.AppendRunTraceAsync(runId, batch, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunTraceResponse(accepted));
    }

    private static async Task<Results<Ok<RunOutcomeResponse>, ProblemHttpResult>> RunOutcomeAsync(
        Guid runId, HttpContext context, Dispatcher dispatcher, CancellationToken ct)
    {
        var request = await ReadBodyAsync<RunOutcomeRequest>(context, OutcomeBodyLimit, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Invalid("the request body must be a run outcome document.");
        }

        if (string.IsNullOrWhiteSpace(request.Node))
        {
            return Invalid("node is required.");
        }

        if (request.Attempt < 1)
        {
            return Invalid("attempt must be the value the hand-out carried (1 or more).");
        }

        if (request.Outcome == RunOutcomeKind.Completed && string.IsNullOrWhiteSpace(request.ArtifactJson))
        {
            return Invalid("a completed outcome must carry the run artifact.");
        }

        var status = await dispatcher.RecordRunOutcomeAsync(runId, request, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunOutcomeResponse(status));
    }

    private static async Task<Results<Ok<TaskOutcomeResponse>, ProblemHttpResult>> TaskOutcomeAsync(
        Guid taskId, HttpContext context, Dispatcher dispatcher, CancellationToken ct)
    {
        var request = await ReadBodyAsync<TaskOutcomeRequest>(context, OutcomeBodyLimit, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Invalid("the request body must be a task outcome document.");
        }

        if (string.IsNullOrWhiteSpace(request.Node))
        {
            return Invalid("node is required.");
        }

        var recorded = await dispatcher.RecordTaskOutcomeAsync(taskId, request, ct).ConfigureAwait(false);
        return TypedResults.Ok(new TaskOutcomeResponse(recorded));
    }

    /// <summary>Reads a body under the route's own size limit rather than the host's default request bound, so a
    /// large but legitimate document (a run artifact, a trace batch of big statements) is accepted and a runaway one
    /// is refused before it is buffered.</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, long limit, CancellationToken ct)
        where T : class
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}
