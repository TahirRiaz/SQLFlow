using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.Invoke;

namespace SqlFlow.SqlServer.Export;

/// <summary>
/// The without-database composition root for export flows: builds a fully wired <see cref="ExportFlowRunner"/>
/// from a flow document's own connection and invoke declarations, with no control database anywhere. The same
/// resolver composition as full mode runs behind an in-memory store; the run log stays the no-op default. With
/// no declared invokes the postInvoke hook stays unavailable (a set alias is a clear error, the documented
/// without-database default). The source is SQL Server by the document loader's guarantee, so the built-in SQL
/// Server provider set is all the resolver needs. Both the CLI and the tests construct through here, so YAML
/// execution has exactly one code path. The caller supplies the write destinations (a cloud destination lives
/// in an Azure assembly this one does not reference, so it arrives as the Core <see cref="IExportDestination"/>
/// abstraction, mirroring how invoke executors arrive); when none are given the runner defaults to local only.
/// </summary>
public static class WithoutDatabaseExport
{
    public static ExportFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        ISecretResolver? secrets = null,
        IEnumerable<InvokeDefinition>? invokes = null,
        IReadOnlyList<IInvokeExecutor>? invokeExecutors = null,
        IReadOnlyList<IExportDestination>? destinations = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var registry = SqlServerSourceProvider.CreateRegistry();
        return new ExportFlowRunner(
            WithoutDatabaseResolver.Build(connections, secrets, registry),
            destinations: destinations,
            invoke: WithoutDatabaseInvokeRunner.Build(invokes, invokeExecutors));
    }
}
