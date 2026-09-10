using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;

namespace SqlFlow.HealthCheck;

/// <summary>
/// The without-database composition root for health-check flows: builds a fully wired
/// <see cref="HealthCheckFlowRunner"/> from a flow document's own connection declarations plus a model store,
/// with no control database anywhere. The same resolver composition as full mode runs behind an in-memory
/// store; the run log stays the no-op default. The server is SQL Server by the document loader's guarantee,
/// so the built-in SQL Server provider set is all the resolver needs. Both the CLI (flow documents AND the
/// ad-hoc 'sqlflow healthcheck' verb) and the tests construct through here, so execution has exactly one code
/// path; the only choice is where models persist (the .sqlflow/state folder, or in memory for ad-hoc runs).
/// </summary>
public static class WithoutDatabaseHealthCheck
{
    /// <param name="connections">The document's declared connections.</param>
    /// <param name="anchorDirectory">The flow document's directory: the trained models persist under
    /// <c>&lt;anchor&gt;/.sqlflow/state/&lt;flow&gt;/&lt;metric&gt;/</c>, beside the run history.</param>
    /// <param name="secrets">Secret resolution for <c>${...}</c> references; environment variables when null.</param>
    public static HealthCheckFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        string anchorDirectory,
        ISecretResolver? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        return BuildRunner(connections, new HealthCheckModelStore(anchorDirectory), secrets);
    }

    /// <summary>The store-explicit form: ad-hoc runs pass an <see cref="EphemeralHealthCheckModelStore"/> so
    /// checking a foreign table leaves nothing behind.</summary>
    public static HealthCheckFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        IHealthCheckModelStore store,
        ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(store);

        var registry = SqlServerSourceProvider.CreateRegistry();
        return new HealthCheckFlowRunner(WithoutDatabaseResolver.Build(connections, secrets, registry), store);
    }
}
