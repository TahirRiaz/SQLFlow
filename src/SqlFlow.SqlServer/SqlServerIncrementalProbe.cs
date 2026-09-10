using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Computes the incremental watermark with a dynamic query against the target. For the file-date watermark it
/// resolves the column's type from the catalog, takes <c>MAX([col])</c> (a date/time column or the raw layer's
/// string-encoded provenance stamp), and applies the day overlap client-side. For the
/// row-level watermark it resolves the column's type to a <see cref="WatermarkKind"/>, returns <c>MAX([col])</c>
/// shifted back by the overlap, and lets the engine compare source rows against it. A missing table or empty
/// result yields null (full load). The table check, the type lookup, and the MAX all run in one batched round
/// trip, and the MAX statement only executes when the live column's type is suitable, so a bad column still
/// fails with the precise engine error rather than a SQL one. No SMO; a fresh connection per call.
/// </summary>
public sealed class SqlServerIncrementalProbe : IIncrementalProbe
{
    public async Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(dateColumn);
        if (overlapDays < 0)
        {
            throw new SqlFlowException($"Incremental overlapDays must be zero or positive, was {overlapDays}.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var escapedColumn = $"[{dateColumn.Replace("]", "]]", StringComparison.Ordinal)}]";
        var probe = await ProbeMaxAsync(
            connection, table, dateColumn,
            $"SELECT MAX({escapedColumn}) FROM {table};",
            FileDateTypeNames,
            overlapDays,
            ct).ConfigureAwait(false);

        // Missing target table -> nothing has been loaded yet -> full load.
        if (probe.ObjectId is null)
        {
            return null;
        }

        if (probe.TypeName is null)
        {
            throw new SqlFlowException($"Incremental dateColumn '{dateColumn}' was not found on {table}.");
        }

        if (!FileDateTypeNames.Contains(probe.TypeName, StringComparer.Ordinal))
        {
            throw new SqlFlowException(
                $"Incremental dateColumn '{dateColumn}' on {table} is type '{probe.TypeName}'; the file-date watermark "
                + "requires a date/time column, a string column, or a numeric column carrying the engine's provenance "
                + "date encoding (yyyyMMddHHmmss).");
        }

        if (probe.Max is null)
        {
            return null;
        }

        // The raw landing layer stamps FileDate_DW as a string (yyyyMMddHHmmss), where lexicographic MAX is
        // chronological MAX, so string columns parse the maximum back to a UTC instant here. Legacy layers (and
        // silver/ods tables carried over from them) store the same yyyyMMddHHmmss stamp in a NUMERIC column
        // (decimal/bigint), whose numeric MAX is also chronological; those parse identically after rendering the
        // integer value as its digit string. Typed columns keep the pre-existing UTC-instant semantics, with
        // datetimeoffset carrying its own offset. The overlap is applied client-side, which is equivalent to the
        // former DATEADD on the server.
        var mark = probe.Max switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            string text => ParseFileDateWatermark(text, dateColumn, table),
            decimal dec => ParseFileDateWatermark(dec.ToString("F0", CultureInfo.InvariantCulture), dateColumn, table),
            long l => ParseFileDateWatermark(l.ToString(CultureInfo.InvariantCulture), dateColumn, table),
            int i => ParseFileDateWatermark(i.ToString(CultureInfo.InvariantCulture), dateColumn, table),
            _ => throw new SqlFlowException(
                $"Incremental dateColumn '{dateColumn}' on {table} produced an unexpected MAX value of type "
                + $"'{probe.Max.GetType().Name}'."),
        };

        return mark.AddDays(-overlapDays);
    }

    public async Task<WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (overlap < 0)
        {
            throw new SqlFlowException($"Incremental watermark overlap must be zero or positive, was {overlap}.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var escapedColumn = $"[{column.Replace("]", "]]", StringComparison.Ordinal)}]";
        var probe = await ProbeMaxAsync(
            connection, table, column,
            $"SELECT MAX({escapedColumn}) FROM {table};",
            WatermarkKinds.Keys,
            overlapDays: 0,
            ct).ConfigureAwait(false);

        // Missing target table -> nothing has been loaded yet -> full load.
        if (probe.ObjectId is null)
        {
            return null;
        }

        if (probe.TypeName is null)
        {
            throw new SqlFlowException($"Incremental watermarkColumn '{column}' was not found on {table}.");
        }

        var kind = WatermarkKindOf(probe.TypeName)
            ?? throw new SqlFlowException(
                $"Incremental watermarkColumn '{column}' on {table} is type '{probe.TypeName}', which is not orderable as a watermark. Use an integer, decimal, float, date/time, or text column.");

        if (probe.Max is null)
        {
            return null;
        }

        return new WatermarkValue { Kind = kind, Value = ApplyOverlap(kind, probe.Max, overlap) };
    }

    private sealed record ProbeResult(int? ObjectId, string? TypeName, object? Max);

    /// <summary>
    /// One round trip: resolves the table's object id and the column's live type, and, when that type is one
    /// of <paramref name="allowedTypes"/>, runs <paramref name="maxSql"/> in the same batch as a second result
    /// set. The MAX goes through <c>sp_executesql</c> because a missing column on an existing table fails the
    /// outer batch at compile time (only a missing table gets deferred name resolution); the guarded inner
    /// batch compiles only after the catalog check passed. The caller keeps the missing-table, missing-column,
    /// and unsuitable-type decisions in C#, exactly as the previous three sequential queries did. No timeout:
    /// MAX over an unindexed column scans the table.
    /// </summary>
    private static async Task<ProbeResult> ProbeMaxAsync(
        SqlConnection connection,
        string table,
        string column,
        string maxSql,
        IReadOnlyCollection<string> allowedTypes,
        int overlapDays,
        CancellationToken ct)
    {
        // The allowed type names are the engine's own fixed vocabulary (never user input), so the IN list
        // is safe to inline.
        var typeList = string.Join(", ", allowedTypes.Select(t => $"'{t}'"));
        var sql = $"""
            DECLARE @objectId int = OBJECT_ID(@t, 'U');
            DECLARE @type sysname = (
                SELECT tp.name
                FROM sys.columns c
                JOIN sys.types tp ON tp.user_type_id = c.user_type_id
                WHERE c.object_id = @objectId AND c.name = @c);
            SELECT @objectId AS ObjectId, @type AS TypeName;
            IF @objectId IS NOT NULL AND @type IN ({typeList})
            BEGIN
                EXEC sp_executesql @maxSql, N'@overlap int', @overlap = @overlap;
            END
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        command.Parameters.AddWithValue("@maxSql", maxSql);
        // Declared for both callers; a maxSql that does not reference @overlap simply ignores it.
        command.Parameters.AddWithValue("@overlap", overlapDays);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new SqlFlowException($"The incremental watermark probe against {table} returned no metadata row.");
        }

        var objectId = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
        var typeName = reader.IsDBNull(1) ? null : reader.GetString(1);

        object? max = null;
        if (await reader.NextResultAsync(ct).ConfigureAwait(false) && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            max = reader.IsDBNull(0) ? null : reader.GetValue(0);
        }

        return new ProbeResult(objectId, typeName, max);
    }

    private static object ApplyOverlap(WatermarkKind kind, object value, double overlap) => kind switch
    {
        WatermarkKind.Whole => Convert.ToInt64(value, CultureInfo.InvariantCulture) - (long)overlap,
        WatermarkKind.Fixed => Convert.ToDecimal(value, CultureInfo.InvariantCulture) - (decimal)overlap,
        WatermarkKind.Floating => Convert.ToDouble(value, CultureInfo.InvariantCulture) - overlap,
        WatermarkKind.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture).AddDays(-overlap),
        WatermarkKind.DateTimeOffset => ((DateTimeOffset)value).AddDays(-overlap),
        WatermarkKind.Text => (string)value,
        _ => throw new SqlFlowException($"Unsupported watermark kind '{kind}'."),
    };

    // The file-date watermark's accepted type names; also gates the batched MAX (only these types run it).
    // String types are accepted because the raw landing layer stamps FileDate_DW as a fixed-length
    // yyyyMMddHHmmss string (see FileSourceReaderBase), whose lexicographic MAX is its chronological MAX. Numeric
    // types (decimal/numeric/bigint/int) are accepted for the same encoding stored as a number, which legacy
    // layers and the silver/ods tables carried over from them commonly use; their numeric MAX is chronological too.
    private static readonly string[] FileDateTypeNames =
        ["date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "char", "varchar", "nchar", "nvarchar",
         "decimal", "numeric", "bigint", "int"];

    /// <summary>Parses a string-typed file-date watermark back to a UTC instant, accepting the encodings the
    /// engine writes plus the common ISO shapes the file-date bound parser accepts on the read side.</summary>
    private static DateTimeOffset ParseFileDateWatermark(string value, string dateColumn, string table)
    {
        string[] formats = ["yyyyMMddHHmmss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd", "yyyyMMdd"];
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, styles, out var exact))
        {
            return new DateTimeOffset(exact);
        }

        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return new DateTimeOffset(parsed);
        }

        throw new SqlFlowException(
            $"Incremental dateColumn '{dateColumn}' on {table} holds '{value}', which is not a recognizable "
            + "provenance date. Expected an engine-stamped value such as yyyyMMddHHmmss.");
    }

    // One table for both concerns: the kind resolution AND the batched MAX gate read the same set, so a
    // type the engine cannot order never executes MAX (and vice versa).
    private static readonly Dictionary<string, WatermarkKind> WatermarkKinds = new(StringComparer.Ordinal)
    {
        ["tinyint"] = WatermarkKind.Whole,
        ["smallint"] = WatermarkKind.Whole,
        ["int"] = WatermarkKind.Whole,
        ["bigint"] = WatermarkKind.Whole,
        ["decimal"] = WatermarkKind.Fixed,
        ["numeric"] = WatermarkKind.Fixed,
        ["money"] = WatermarkKind.Fixed,
        ["smallmoney"] = WatermarkKind.Fixed,
        ["real"] = WatermarkKind.Floating,
        ["float"] = WatermarkKind.Floating,
        ["date"] = WatermarkKind.DateTime,
        ["datetime"] = WatermarkKind.DateTime,
        ["datetime2"] = WatermarkKind.DateTime,
        ["smalldatetime"] = WatermarkKind.DateTime,
        ["datetimeoffset"] = WatermarkKind.DateTimeOffset,
        ["char"] = WatermarkKind.Text,
        ["varchar"] = WatermarkKind.Text,
        ["nchar"] = WatermarkKind.Text,
        ["nvarchar"] = WatermarkKind.Text,
    };

    private static WatermarkKind? WatermarkKindOf(string typeName)
        => WatermarkKinds.TryGetValue(typeName, out var kind) ? kind : null;
}
