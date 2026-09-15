using SqlFlow.ControlPlane.Hosting;

namespace SqlFlow.ControlPlane;

/// <summary>
/// SQLFlow's own control plane host: the full control plane with no modules. A host that composes modules has a program of
/// its own that calls <see cref="ControlPlaneHost.RunAsync"/> with them. Public so the integration tests can host the app
/// through WebApplicationFactory.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static Task Main(string[] args) => ControlPlaneHost.RunAsync(args);
}
