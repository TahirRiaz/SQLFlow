using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// The credential an external trigger (a GitHub workflow, a key holder) gets from an exchange
/// (<see cref="TokenIssuer.IssueTriggerCredential"/>), and the fence around it.
/// <para>Every signed-in principal may read and operate the whole product (the two-tier privilege model), so a trigger's
/// credential is marked with <see cref="ClaimType"/> and refused on every endpoint that has not opted in with
/// <see cref="AllowExternalTrigger"/> (<see cref="ExternalTriggerCredentialMiddleware"/>). The few that have (making a
/// trigger request, and reading the requests the trigger made) check the trigger's id (<see cref="TriggerIdClaim"/>)
/// themselves. The credential carries no scope, no role, no user and no <c>auth_time</c>, so it cannot be renewed, mint a
/// personal access token, approve a device sign-in, or reach anything else.</para>
/// </summary>
public static class ExternalTriggerCredential
{
    /// <summary>The claim that marks a token's use; shared with the other issued credentials.</summary>
    public const string ClaimType = AssistantDelegation.ClaimType;

    /// <summary>The <see cref="ClaimType"/> value a trigger's credential carries.</summary>
    public const string ClaimValue = "trigger";

    /// <summary>The claim naming the trigger the credential was issued to.</summary>
    public const string TriggerIdClaim = "trigger_id";

    /// <summary>The claim carrying where the caller came from, as its proof stated it (for GitHub: the repository,
    /// workflow, ref, commit, run and actor), which a request records.</summary>
    public const string OriginClaim = "trigger_origin";

    /// <summary>The claim carrying a link back to the caller's job (for GitHub: the workflow run's attempt).</summary>
    public const string OriginUrlClaim = "trigger_origin_url";

    /// <summary>The problem title a refused call answers with.</summary>
    public const string DeniedTitle = "Not available to an external trigger";

    /// <summary>Whether <paramref name="user"/> authenticated with a credential issued to an external trigger.</summary>
    public static bool IsTriggerCredential(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasClaim(ClaimType, ClaimValue);
    }

    /// <summary>The trigger a credential was issued to, or null for any other principal (and for a trigger credential
    /// without a readable id, which then matches no trigger).</summary>
    public static Guid? TriggerId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return IsTriggerCredential(user) && Guid.TryParse(user.FindFirst(TriggerIdClaim)?.Value, out var id) ? id : null;
    }

    /// <summary>Opens an endpoint to a trigger's credential. The endpoint itself must check that what it acts on belongs
    /// to the trigger (<see cref="TriggerId"/>).</summary>
    public static TBuilder AllowExternalTrigger<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(ExternalTriggerAccess.Instance);
    }
}

/// <summary>Endpoint metadata marking a route as reachable with an external trigger's credential.</summary>
public sealed class ExternalTriggerAccess
{
    private ExternalTriggerAccess()
    {
    }

    public static ExternalTriggerAccess Instance { get; } = new();
}

/// <summary>Refuses an external trigger's credential on every endpoint not opened to it with
/// <see cref="ExternalTriggerCredential.AllowExternalTrigger"/>. Runs after authorization, so the principal is resolved;
/// every other request passes straight through.</summary>
public sealed class ExternalTriggerCredentialMiddleware
{
    private readonly RequestDelegate _next;

    public ExternalTriggerCredentialMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ExternalTriggerCredential.IsTriggerCredential(context.User)
            && context.GetEndpoint()?.Metadata.GetMetadata<ExternalTriggerAccess>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: ExternalTriggerCredential.DeniedTitle,
                detail: "An external trigger's credential makes trigger requests for its repository and reads the requests it made; nothing else.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
