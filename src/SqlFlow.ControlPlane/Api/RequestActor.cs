using System.Security.Claims;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// Who is calling, as the control plane's attributed writes record it: the token's subject (<c>sub</c>), else the
/// identity's name. The one rule every endpoint that records a requester uses (a run trigger, a compute task, a
/// prepared query, a pool scale), and what a host module's endpoints use for their own audit trails.
/// </summary>
public static class RequestActor
{
    /// <summary>The label an audit trail records when the caller carries no name.</summary>
    public const string Unknown = "unknown";

    /// <summary>The caller's name, trimmed, or null when the principal carries none (a token without a subject or a
    /// name).</summary>
    public static string? Of(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>The caller as an actor label, <c>user:&lt;name&gt;</c>, or <c>user:unknown</c> when the principal carries
    /// no name, for trails that also record actors that are not people (a schedule, a node).</summary>
    public static string Label(ClaimsPrincipal user) => "user:" + (Of(user) ?? Unknown);
}
