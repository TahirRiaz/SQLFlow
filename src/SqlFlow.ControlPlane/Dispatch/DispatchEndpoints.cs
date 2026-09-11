using Microsoft.AspNetCore.Http.HttpResults;
using SqlFlow.Dispatch;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>The dispatcher's diagnostic read surface: <c>GET /api/v1/dispatch</c> returns the queue as the
/// dispatcher sees it (every queued run with the gate holding it back, every lease with its node and expiry, the
/// fleet, ownership, and the last housekeeping passes). On a passive replica it reports <c>active: false</c> with an
/// empty queue, which is itself the diagnosis.</summary>
public static class DispatchEndpoints
{
    public static RouteGroupBuilder MapDispatchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/dispatch", GetSnapshot).WithTags("Dispatch").WithName("GetDispatchSnapshot");
        return group;
    }

    private static Ok<DispatchSnapshot> GetSnapshot(Dispatcher dispatcher) => TypedResults.Ok(dispatcher.Snapshot());
}
