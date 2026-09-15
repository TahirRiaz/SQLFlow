namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// What a module sees once the control plane is built: the application's services and the authenticated
/// <c>/api/v1</c> route groups, one per authorization tier, each carrying the same policy as SQLFlow's own endpoints of that
/// tier. The groups belong to the module (conventions a module adds to them never reach SQLFlow's endpoints). Handed to
/// <see cref="IControlPlaneModule.MapEndpoints"/>.
/// </summary>
public sealed class ControlPlaneModuleEndpoints
{
    internal ControlPlaneModuleEndpoints(string moduleName, IServiceProvider services, RouteGroupBuilder api)
    {
        ModuleName = moduleName;
        Services = services;
        Read = api.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Read);
        Operate = api.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Operate);
        Author = api.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Author);
        Admin = api.MapGroup(string.Empty).RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>The name of the module mapping its endpoints.</summary>
    public string ModuleName { get; }

    /// <summary>The built application's root service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary><c>/api/v1</c> under the <see cref="ControlPlanePolicies.Read"/> policy: any signed-in user.</summary>
    public RouteGroupBuilder Read { get; }

    /// <summary><c>/api/v1</c> under the <see cref="ControlPlanePolicies.Operate"/> policy: running and changing things.</summary>
    public RouteGroupBuilder Operate { get; }

    /// <summary><c>/api/v1</c> under the <see cref="ControlPlanePolicies.Author"/> policy: authoring changes for review.</summary>
    public RouteGroupBuilder Author { get; }

    /// <summary><c>/api/v1</c> under the <see cref="ControlPlanePolicies.Admin"/> policy: the <c>admin</c> scope only.</summary>
    public RouteGroupBuilder Admin { get; }
}
