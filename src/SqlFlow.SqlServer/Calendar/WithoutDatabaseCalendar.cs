using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer.Calendar;

/// <summary>
/// The without-database composition root for calendar flows: builds a fully wired
/// <see cref="CalendarFlowRunner"/> from a flow document's own connection declarations, with no control database
/// anywhere. The same resolver composition as full mode runs behind an in-memory store and the run log stays the
/// no-op default. The server is SQL Server by the document loader's guarantee, so the built-in SQL Server
/// provider set is all the resolver needs. Both the CLI and the tests construct through here, so YAML execution
/// has exactly one code path.
/// </summary>
public static class WithoutDatabaseCalendar
{
    public static CalendarFlowRunner BuildRunner(IEnumerable<DataSource> connections, ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var registry = SqlServerSourceProvider.CreateRegistry();
        return new CalendarFlowRunner(WithoutDatabaseResolver.Build(connections, secrets, registry));
    }
}
