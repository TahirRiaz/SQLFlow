using Microsoft.Data.SqlClient;

namespace SqlFlow.SqlServer;

/// <summary>
/// Connection-level tuning for the bulk-load path. A bulk copy moves large volumes in few, large TDS packets,
/// so the default 8 KB packet size leaves throughput on the table; raising it to the protocol maximum cuts
/// round-trips. Only the default is raised, so an operator who pinned a packet size keeps it.
/// </summary>
internal static class BulkTuning
{
    /// <summary>The TDS protocol maximum packet size; the documented bulk-load sweet spot (bcp/SSIS use it).</summary>
    private const int MaxPacketSize = 32767;

    /// <summary>The default packet size at or below which we raise to the maximum (8 KB is the SqlClient default).</summary>
    private const int DefaultPacketSize = 8000;

    /// <summary>Returns the connection string with the packet size raised to the protocol maximum when it was
    /// left at (or below) the default; a custom packet size is preserved.</summary>
    public static string ForBulk(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.PacketSize <= DefaultPacketSize)
        {
            builder.PacketSize = MaxPacketSize;
        }

        return builder.ConnectionString;
    }
}
