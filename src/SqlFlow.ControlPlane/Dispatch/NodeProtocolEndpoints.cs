using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// The node protocol under <c>/api/v1/node</c>, the only way a compute node obtains or reports work. Every call is
/// node-initiated and authenticated with a bearer credential carrying the <c>node</c> scope. <c>POST /poll</c> is
/// the heartbeat, lease renewal, cancel channel and hand-out in one long-polled call; <c>POST /runs/{id}/outcome</c>
/// and <c>POST /tasks/{id}/outcome</c> report results under the hand-out's fence. A replica whose dispatcher is
/// not the owner answers 503 with a retry hint (see <see cref="Infrastructure.GlobalExceptionHandler"/>), so a node
/// behind a load balancer lands on the owner within a retry or two.
/// </summary>
public static class NodeProtocolEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Room for the artifact plus the envelope around it.</summary>
    private const long OutcomeBodyLimit = NodeProtocol.MaxArtifactBytes + (4L * 1024 * 1024);

    public static RouteGroupBuilder MapNodeProtocolEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/poll", PollAsync).WithTags("Node").WithName("NodePoll");
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

    private static async Task<Results<Ok<RunOutcomeResponse>, ProblemHttpResult>> RunOutcomeAsync(
        Guid runId, HttpContext context, Dispatcher dispatcher, CancellationToken ct)
    {
        var request = await ReadOutcomeAsync<RunOutcomeRequest>(context, ct).ConfigureAwait(false);
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
        var request = await ReadOutcomeAsync<TaskOutcomeRequest>(context, ct).ConfigureAwait(false);
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

    /// <summary>Reads an outcome body under the artifact size limit rather than the host's default request bound,
    /// so a large but legitimate run.json is accepted and a runaway one is refused before it is buffered.</summary>
    private static async Task<T?> ReadOutcomeAsync<T>(HttpContext context, CancellationToken ct)
        where T : class
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = OutcomeBodyLimit;
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
