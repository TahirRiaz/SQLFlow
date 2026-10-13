using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// The credential the control plane itself issues to a worker it starts (<see cref="TokenIssuer.IssueNodeCredential"/>),
/// and the fences around it. Every signed-in principal may read and operate the product (the two-tier privilege model),
/// so a worker's credential is marked with <see cref="ClaimType"/> and refused everywhere but the node protocol
/// (<see cref="NodeCredentialMiddleware"/>), and it is bound to the node it was issued for (<see cref="NodeClaimType"/>):
/// the node header and every node name in a node protocol request must be that node's (<see cref="BindingFilter"/>). A
/// credential that lives in one worker's environment can poll for that worker's work and report it, and nothing else.
/// </summary>
public static class NodeCredential
{
    /// <summary>The claim that marks a token's use; shared with the assistant's delegated tokens.</summary>
    public const string ClaimType = AssistantDelegation.ClaimType;

    /// <summary>The <see cref="ClaimType"/> value a worker's issued credential carries.</summary>
    public const string ClaimValue = "node";

    /// <summary>The claim naming the one node a worker's credential is for.</summary>
    public const string NodeClaimType = "node_name";

    /// <summary>The subject of a worker's credential, before the node's name.</summary>
    public const string SubjectPrefix = "managed-worker:";

    /// <summary>The problem title a refused call answers with.</summary>
    public const string DeniedTitle = "Not available to a worker's credential";

    /// <summary>Whether <paramref name="user"/> authenticated with a credential issued to a worker.</summary>
    public static bool IsNodeCredential(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasClaim(ClaimType, ClaimValue);
    }

    /// <summary>The node a worker's credential is bound to, or null for any other principal. A worker's credential with no
    /// node claim is bound to no node at all, so it matches none.</summary>
    public static string? BoundNode(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return IsNodeCredential(user) ? user.FindFirst(NodeClaimType)?.Value ?? string.Empty : null;
    }

    /// <summary>On the node protocol's routes: refuses a worker's credential when a request names any other node than the
    /// one it was issued for. Every other principal passes straight through.</summary>
    public static async ValueTask<object?> BindingFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (BoundNode(context.HttpContext.User) is { } bound)
        {
            foreach (var argument in context.Arguments)
            {
                var named = argument switch
                {
                    NodePollRequest request => request.Node,
                    RunContextRequest request => request.Node,
                    RunTraceBatch batch => batch.Node,
                    FanOutRequest request => request.Node,
                    FanOutFence fence => fence.Node,
                    RunOutcomeRequest request => request.Node,
                    TaskOutcomeRequest request => request.Node,
                    _ => null,
                };
                if (named is not null && !string.Equals(named, bound, StringComparison.Ordinal))
                {
                    return Denied("A worker's credential speaks for the node it was issued for only.");
                }
            }
        }

        return await next(context).ConfigureAwait(false);
    }

    internal static IResult Denied(string detail)
        => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: DeniedTitle, detail: detail);
}

/// <summary>Refuses a worker's issued credential on every route outside the node protocol (<c>/api/v1/node</c>), and on
/// the node protocol when its node header names any other node than the one it was issued for. Runs after authorization,
/// so the principal is resolved; every other request passes straight through.</summary>
public sealed class NodeCredentialMiddleware
{
    /// <summary>The problem title a refused call answers with.</summary>
    public const string DeniedTitle = NodeCredential.DeniedTitle;

    private static readonly PathString NodeRoutes = new(NodeProtocol.RoutePrefix);

    private readonly RequestDelegate _next;

    public NodeCredentialMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (NodeCredential.BoundNode(context.User) is { } bound)
        {
            string? refusal = null;
            if (!context.Request.Path.StartsWithSegments(NodeRoutes))
            {
                refusal = "A credential the control plane issued to a worker it started speaks the node protocol only.";
            }
            else if (!string.Equals(context.Request.Headers[NodeProtocol.NodeHeader].ToString(), bound, StringComparison.Ordinal))
            {
                refusal = "A worker's credential speaks for the node it was issued for only.";
            }

            if (refusal is not null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await NodeCredential.Denied(refusal).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
