using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One operation a registered flow kind offers: its name, label, description, and whether it writes the
/// flow's target.</summary>
public sealed record FlowKindOperationDto(string Name, string Label, string Description, bool WritesTarget);

/// <summary>A flow kind a host module registered: its <c>flowType</c>, what it does, the operations a run of it can
/// perform, and the default operation (the first declared, or null when the kind declares none).</summary>
public sealed record FlowKindDto(
    string FlowType, string Description, IReadOnlyList<FlowKindOperationDto> Operations, string? DefaultOperation);

/// <summary>
/// The registered flow kinds, so a client can offer a kind's operations when it triggers a run or defines a schedule
/// without knowing the kinds a host was built with. Only kinds a host registered are listed: the built-in kinds take no
/// operation. Mapped under the "read" scope.
/// </summary>
public static class KindEndpoints
{
    public static RouteGroupBuilder MapKindEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/kinds", ListKinds).WithTags("Kinds").WithName("ListFlowKinds");
        return group;
    }

    private static Ok<List<FlowKindDto>> ListKinds(YamlDocumentLoader documents)
        => TypedResults.Ok(documents.Kinds
            .OrderBy(k => k.FlowType, StringComparer.OrdinalIgnoreCase)
            .Select(k => new FlowKindDto(
                k.FlowType,
                k.Description,
                k.Operations.Select(o => new FlowKindOperationDto(o.Name, o.Label, o.Description, o.WritesTarget)).ToList(),
                k.Operations.Count > 0 ? k.Operations[0].Name : null))
            .ToList());
}
