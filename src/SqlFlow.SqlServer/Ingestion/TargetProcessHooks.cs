using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// Runs the legacy PreProcessOnTrg / PostProcessOnTrg hooks: the stored value is a raw T-SQL command (typically
/// <c>EXEC [schema].[proc]</c>), executed verbatim as <see cref="CommandType.Text"/> on the TARGET connection,
/// with no parameters bound and outside the load transaction. Mirrors the legacy
/// <c>ExecNonQuery(trgSqlCon, value, timeout)</c> call and its <c>Length &gt; 2</c> activation gate (a trimmed
/// value of two characters or fewer is skipped).
/// </summary>
internal static class TargetProcessHooks
{
    internal static bool ShouldRun(string? hook) => hook is not null && hook.Trim().Length > 2;

    internal static async Task RunAsync(string targetConnectionString, string hook, CancellationToken ct)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(hook, connection) { CommandType = CommandType.Text, CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
