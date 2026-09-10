using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.Invoke;

namespace SqlFlow.SqlServer.StoredProcedures;

/// <summary>
/// The without-database composition root for stored-procedure flows: builds a fully wired
/// <see cref="StoredProcedureFlowRunner"/> from a flow document's own connection and invoke declarations, with
/// no control database anywhere. The same resolver composition as full mode runs behind an in-memory store; the
/// run log stays the no-op default. With no declared invokes the postInvoke hook stays unavailable (a set alias
/// is a clear error, the documented without-database default). The server is SQL Server by the document
/// loader's guarantee, so the built-in SQL Server provider set is all the resolver needs. Both the CLI and the
/// tests construct through here, so YAML execution has exactly one code path.
/// </summary>
public static class WithoutDatabaseStoredProcedure
{
    public static StoredProcedureFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        ISecretResolver? secrets = null,
        IEnumerable<InvokeDefinition>? invokes = null,
        IReadOnlyList<IInvokeExecutor>? invokeExecutors = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var registry = SqlServerSourceProvider.CreateRegistry();
        return new StoredProcedureFlowRunner(
            WithoutDatabaseResolver.Build(connections, secrets, registry),
            invoke: WithoutDatabaseInvokeRunner.Build(invokes, invokeExecutors));
    }
}
