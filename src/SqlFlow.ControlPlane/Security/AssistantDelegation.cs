using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// The credential the control plane hands an AI assistant run, and the fence around it.
/// <para>A chat question is answered by a model hosted at a third party (Azure AI Foundry, OpenAI, Anthropic), whose
/// runtime calls the SQLFlow MCP server with whatever bearer the control plane gave it. Handing over the signed-in
/// user's own session token would give that third party everything the session can do: renew it for weeks, mint a
/// never-expiring personal access token, approve a device sign-in, trigger runs. So each run instead gets a
/// <em>delegated</em> token: issued for the same user, valid only for the run's own timeout, never renewable (no
/// <c>auth_time</c>), and marked with <see cref="ClaimType"/> so the control plane can refuse it everywhere the
/// assistant has no business being.</para>
/// <para>The fence is default-deny. <see cref="AssistantDelegationMiddleware"/> rejects a delegated token on every
/// endpoint that does not carry <see cref="AssistantAccess"/> metadata, and on a read-only opt-in it admits only safe
/// methods. A new endpoint is therefore closed to the assistant until someone decides otherwise, rather than open
/// until someone remembers to close it.</para>
/// </summary>
public static class AssistantDelegation
{
    /// <summary>The claim that marks a token as delegated to an assistant run.</summary>
    public const string ClaimType = "token_use";

    /// <summary>The <see cref="ClaimType"/> value an assistant run's token carries.</summary>
    public const string ClaimValue = "assistant";

    /// <summary>The slack added to the run timeout when sizing a delegated token's lifetime, so a tool call made in
    /// the last moments of a run is not refused over the gap between the control plane's clock and the token's
    /// issue time. Small by design: the lifetime is what bounds a leaked token.</summary>
    public static readonly TimeSpan LifetimeGrace = TimeSpan.FromSeconds(60);

    /// <summary>The scopes a delegated token may never carry, whatever its user holds. Administration and fleet
    /// traffic have no place in an assistant run.</summary>
    private static readonly string[] WithheldScopes = ["admin", "node"];

    /// <summary>Whether <paramref name="user"/> authenticated with a token delegated to an assistant run.</summary>
    public static bool IsDelegated(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasClaim(ClaimType, ClaimValue);
    }

    /// <summary>The caller's scopes with the ones a delegated token must never carry removed.</summary>
    public static IReadOnlyList<string> DelegableScopes(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (user.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !WithheldScopes.Contains(s, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Opens an endpoint (or a whole route group) to delegated tokens for safe methods only
    /// (GET, HEAD, OPTIONS).</summary>
    public static TBuilder AllowAssistantRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(AssistantAccess.ReadOnly);
    }

    /// <summary>Opens one endpoint to delegated tokens whatever its method. Reserved for the few non-GET calls an
    /// assistant tool genuinely makes, each of which must itself be read-only against the estate.</summary>
    public static TBuilder AllowAssistantWrite<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(AssistantAccess.AnyMethod);
    }
}

/// <summary>Endpoint metadata marking a route as reachable by a delegated assistant token. The most specific entry
/// wins, so a route-level <see cref="AnyMethod"/> widens a group-level <see cref="ReadOnly"/>.</summary>
public sealed class AssistantAccess
{
    private AssistantAccess(bool allowsUnsafeMethods) => AllowsUnsafeMethods = allowsUnsafeMethods;

    /// <summary>Safe methods only.</summary>
    public static AssistantAccess ReadOnly { get; } = new(allowsUnsafeMethods: false);

    /// <summary>Any method.</summary>
    public static AssistantAccess AnyMethod { get; } = new(allowsUnsafeMethods: true);

    /// <summary>Whether POST, PUT, PATCH and DELETE are admitted, not just the safe methods.</summary>
    public bool AllowsUnsafeMethods { get; }
}

/// <summary>
/// Refuses a delegated assistant token on any endpoint it has not been explicitly opened to. Runs after
/// authorization, so the principal is resolved and an anonymous or otherwise unauthorized request has already been
/// answered by the normal policies; every credential that is not delegated passes straight through.
/// </summary>
public sealed class AssistantDelegationMiddleware
{
    private readonly RequestDelegate _next;

    public AssistantDelegationMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AssistantDelegation.IsDelegated(context.User) && !IsAdmitted(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: DeniedTitle,
                detail: "This credential was issued for a single AI assistant run and may only use the read surface " +
                    "the assistant's tools need. It cannot manage tokens, renew or approve sign-ins, start work, " +
                    "or reach any other part of SQLFlow.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>The problem title a refused delegated call answers with, so clients and tests can tell this refusal
    /// apart from an endpoint's own 403.</summary>
    public const string DeniedTitle = "Not available to an assistant run";

    private static bool IsAdmitted(HttpContext context)
    {
        var access = context.GetEndpoint()?.Metadata.GetMetadata<AssistantAccess>();
        if (access is null)
        {
            return false;
        }

        return access.AllowsUnsafeMethods
            || HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method);
    }
}
