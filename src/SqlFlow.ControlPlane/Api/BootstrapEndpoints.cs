using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Where the start-up pass stands: done when every step is, and each step as it last stood.</summary>
public sealed record BootstrapDto(bool Completed, IReadOnlyList<BootstrapStepDto> Steps);

/// <summary>
/// One step of the start-up pass: its name, state (<c>pending</c>, <c>running</c>, <c>done</c>, <c>failed</c>, <c>refused</c>),
/// what it did, the error it last failed with (secret-redacted), how many attempts it took, and when it last changed.
/// </summary>
public sealed record BootstrapStepDto(string Name, string State, string? Summary, string? Error, int Attempts, DateTime? ChangedUtc);

/// <summary>
/// <c>GET /api/v1/bootstrap</c>: the start-up pass step by step, so whoever deployed the control plane, or the deployment
/// that waits for it, sees which step it is on and why one failed. Readiness says only which step it waits for, since a
/// readiness probe answers anyone; this answers a caller with read access.
/// </summary>
public static class BootstrapEndpoints
{
    public static RouteGroupBuilder MapBootstrapEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/bootstrap", Get).WithTags("Bootstrap").WithName("GetBootstrap");
        return group;
    }

    private static Ok<BootstrapDto> Get(BootstrapProgress progress)
    {
        var steps = progress.Steps;
        return TypedResults.Ok(new BootstrapDto(
            progress.Completed,
            [.. steps.Select(s => new BootstrapStepDto(s.Name, s.State, s.Summary, s.Error, s.Attempts, s.ChangedUtc))]));
    }
}
