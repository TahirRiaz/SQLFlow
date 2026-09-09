using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.SqlServer;

/// <summary>
/// Reads an acquisition's resume point from SQL Server by running the flow's own query.
/// </summary>
/// <remarks>
/// Deliberately thin: the statement comes from the flow verbatim, so no table, column, or type is named here and
/// this stays correct for any source's target shape. One column is a flow-wide watermark; two is one per entity,
/// keyed by the first. A date/time value is rendered round-trip ("o") so the text the flow templates into its next
/// request is culture-independent and orders the same way it compares; everything else renders invariantly.
/// </remarks>
public sealed class SqlServerAcquireWatermarkProbe : IAcquireWatermarkProbe
{
    public async Task<IReadOnlyList<AcquireWatermarkRow>> ReadAsync(string connection, string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync(ct).ConfigureAwait(false);

        await using var command = sql.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (reader.FieldCount is not (1 or 2))
        {
            throw new SqlFlowException(
                $"A watermark query must return one column (a flow-wide watermark) or two (entity key, watermark); this one returns {reader.FieldCount}.");
        }

        var keyed = reader.FieldCount == 2;
        var rows = new List<AcquireWatermarkRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var value = Render(reader.IsDBNull(keyed ? 1 : 0) ? null : reader.GetValue(keyed ? 1 : 0));
            var key = keyed && !reader.IsDBNull(0) ? Render(reader.GetValue(0)) : null;
            rows.Add(new AcquireWatermarkRow(key, value));
        }

        return rows;
    }

    private static string? Render(object? value) => value switch
    {
        null or DBNull => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
