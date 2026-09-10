using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Providers.Oracle;

/// <summary>Opens pooled Oracle connections (Oracle.ManagedDataAccess, the official managed driver).</summary>
public sealed class OracleProviderConnectionFactory : IProviderConnectionFactory
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.Oracle;

    public async Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var oracleConnection = new OracleConnection(connection.CanonicalString);
        try
        {
            await oracleConnection.OpenAsync(ct).ConfigureAwait(false);
            return oracleConnection;
        }
        catch
        {
            await oracleConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Canonicalizes Oracle connection strings through <see cref="OracleConnectionStringBuilder"/> and enforces
/// the secretless gate. Oracle has no self-authenticating connection-string mode in this engine, so a literal
/// inline string can never satisfy <see cref="SecretlessPolicy.RequireSelfAuthenticating"/>: an Oracle
/// connection must arrive as a whole <c>${...}</c> secret reference or through a registry entry whose
/// credential mode trusts the value. The managed driver's builder exposes no application-name key, so unlike
/// the MySQL and PostgreSQL canonicalizers this one does not tag the connection with the role; the redaction
/// (username and password stripped) is what matters for the secretless contract.
/// </summary>
public sealed class OracleConnectionStringCanonicalizer : IConnectionStringCanonicalizer
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.Oracle;

    public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        OracleConnectionStringBuilder builder;
        try
        {
            builder = new OracleConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Malformed Oracle connection string: {ex.Message}", ex);
        }

        switch (policy)
        {
            case SecretlessPolicy.Trusted:
                break; // expanded from a whole ${...} secret reference
            case SecretlessPolicy.RejectRestingSecret when string.IsNullOrEmpty(builder.Password):
                break;
            case SecretlessPolicy.RejectRestingSecret:
                throw new SqlFlowException(
                    "Oracle connection strings must not contain a resting password. Supply the whole string as a ${...} secret reference.");
            default:
                throw new SqlFlowException(
                    "Oracle has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.");
        }

        var canonical = builder.ConnectionString;

        var safe = new OracleConnectionStringBuilder(canonical) { Password = string.Empty, UserID = string.Empty };
        return new CanonicalConnection { Canonical = canonical, Redacted = safe.ConnectionString };
    }
}

/// <summary>
/// The Oracle source dialect: double-quote quoting (a literal quote doubles; quoting also preserves the case
/// of introspected identifiers, which unquoted Oracle would fold to upper case), OWNER.NAME qualification
/// where the schema part is the Oracle owner (user), day arithmetic through <c>NUMTODSINTERVAL</c> (which
/// works for both DATE and TIMESTAMP columns and is not bounded by an interval literal's default precision),
/// and <c>HEXTORAW</c> binary literals.
/// </summary>
public sealed class OracleSourceDialect : ISourceSqlDialect
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.Oracle;

    public string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public string QualifyObject(RelationalObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
    }

    public string DateSubtractDays(string expression, int days)
        => $"({expression} - NUMTODSINTERVAL({days.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 'DAY'))";

    public string FormatBinaryLiteral(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"HEXTORAW('{Convert.ToHexString(value)}')";
    }

    // Oracle resolves an unquoted date/time string through the session's NLS format, which is not ISO-8601 by
    // default (a bare '2024-01-03 10:00:00.000' raises ORA-01843). Wrap the ISO body in an explicit conversion
    // with a matching format mask so the watermark comparison is deterministic regardless of session settings.
    public string FormatTemporalLiteral(string baseType, string isoBody)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        ArgumentNullException.ThrowIfNull(isoBody);

        return baseType.ToLowerInvariant() switch
        {
            "date" => $"TO_DATE('{isoBody}', 'YYYY-MM-DD')",
            "datetime" or "datetime2" or "smalldatetime" => $"TO_TIMESTAMP('{isoBody}', 'YYYY-MM-DD HH24:MI:SS.FF3')",
            "datetimeoffset" => $"TO_TIMESTAMP_TZ('{isoBody}', 'YYYY-MM-DD HH24:MI:SS.FF3 TZH:TZM')",
            _ => $"'{isoBody}'",
        };
    }
}
