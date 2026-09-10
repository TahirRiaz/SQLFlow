using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Resolves the target server's locale in one round-trip: the session's effective date order
/// (<c>sys.dm_exec_sessions.date_format</c>, which derives from the login's / server's default
/// language) and the server collation's <c>LCID</c> (for the decimal/grouping convention). The pure
/// mapping is split out so it can be unit-tested without a database.
/// </summary>
public sealed class SqlServerLocaleProvider : IServerLocaleProvider
{
    public async Task<ServerLocale> GetServerLocaleAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT
                CONVERT(int, SERVERPROPERTY('LCID')) AS Lcid,
                CONVERT(sysname, s.date_format)      AS DateFormat
            FROM sys.dm_exec_sessions s
            WHERE s.session_id = @@SPID;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ServerLocale.Invariant;
        }

        var lcid = reader.IsDBNull(0) ? (int?)null : Convert.ToInt32(reader[0], CultureInfo.InvariantCulture);
        var dateFormat = reader.IsDBNull(1) ? null : reader.GetString(1);

        return MapLocale(lcid, dateFormat);
    }

    /// <summary>
    /// Maps the server's LCID and session date_format to a <see cref="ServerLocale"/>. The date order
    /// comes from <c>date_format</c> (authoritative for how the session parses ambiguous dates); the
    /// decimal/grouping convention comes from the LCID's culture. Unknown values fall back to invariant.
    /// </summary>
    internal static ServerLocale MapLocale(int? lcid, string? dateFormat)
    {
        var order = ParseDateOrder(dateFormat);

        var culture = ResolveCulture(lcid);
        if (culture is null)
        {
            return ServerLocale.Invariant with { DateOrder = order };
        }

        return LocaleConversion.FromCulture(culture, order);
    }

    private static DateOrder ParseDateOrder(string? dateFormat)
    {
        if (string.IsNullOrWhiteSpace(dateFormat))
        {
            return DateOrder.Ymd;
        }

        // SQL Server date_format is one of mdy/dmy/ymd/ydm/myd/dym; the first component is decisive.
        return char.ToLowerInvariant(dateFormat.Trim()[0]) switch
        {
            'd' => DateOrder.Dmy,
            'm' => DateOrder.Mdy,
            _ => DateOrder.Ymd,
        };
    }

    private static CultureInfo? ResolveCulture(int? lcid)
    {
        // 127 is the invariant LCID; treat missing or invariant as no usable culture.
        if (lcid is null or 127 or 0)
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(lcid.Value);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
