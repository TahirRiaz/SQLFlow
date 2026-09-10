using System.Data.Common;
using MySqlConnector;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Providers.MySql;

/// <summary>Opens pooled MySQL connections (MySqlConnector, true async I/O).</summary>
public sealed class MySqlProviderConnectionFactory : IProviderConnectionFactory
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public async Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var mySqlConnection = new MySqlConnection(connection.CanonicalString);
        try
        {
            await mySqlConnection.OpenAsync(ct).ConfigureAwait(false);
            return mySqlConnection;
        }
        catch
        {
            await mySqlConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Canonicalizes MySQL connection strings through <see cref="MySqlConnectionStringBuilder"/> and enforces the
/// secretless gate. MySQL has no self-authenticating connection-string mode, so a literal inline string can
/// never satisfy <see cref="SecretlessPolicy.RequireSelfAuthenticating"/>: a MySQL connection must arrive as a
/// whole <c>${...}</c> secret reference or through a registry entry whose credential mode trusts the value.
/// The engine also owns the wire-to-CLR mapping rather than leaving it to the caller's string: the canonical
/// form always reads CHAR(36) as text (<c>GuidFormat=None</c>), so values arrive as the type their declared
/// column type promises.
/// </summary>
public sealed class MySqlConnectionStringCanonicalizer : IConnectionStringCanonicalizer
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Malformed MySQL connection string: {ex.Message}", ex);
        }

        switch (policy)
        {
            case SecretlessPolicy.Trusted:
                break; // expanded from a whole ${...} secret reference
            case SecretlessPolicy.RejectRestingSecret when string.IsNullOrEmpty(builder.Password):
                break;
            case SecretlessPolicy.RejectRestingSecret:
                throw new SqlFlowException(
                    "MySQL connection strings must not contain a resting password. Supply the whole string as a ${...} secret reference.");
            default:
                throw new SqlFlowException(
                    "MySQL has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.");
        }

        builder.ApplicationName = role == ConnectionRole.Source ? "SQLFlow Source" : "SQLFlow Target";

        // The reader must hand back the CLR type the source catalog's DECLARED type promises, because the
        // staging table is built from that declaration (char(36) -> nchar(36), per MySqlSourceTypeMapper).
        // MySqlConnector's default reinterprets every CHAR(36) column as a CLR Guid, which breaks that contract
        // two ways: SqlBulkCopy refuses to write a Guid into the nchar staging column ("The given value ... of
        // type Guid from the data source cannot be converted to type nchar"), and a CHAR(36) that is not a GUID
        // at all fails to parse in the reader. MySQL has no UUID type - a CHAR(36) is text - so it is read as
        // text and lands verbatim; a target column that really is uniqueidentifier still gets one, converted by
        // SQL Server on the insert. OldGuids is the deprecated spelling of the same reinterpretation and is
        // rejected alongside an explicit GuidFormat, so it is cleared with it.
        builder.OldGuids = false;
        builder.GuidFormat = MySqlGuidFormat.None;
        var canonical = builder.ConnectionString;

        var safe = new MySqlConnectionStringBuilder(canonical) { Password = string.Empty, UserID = string.Empty };
        return new CanonicalConnection { Canonical = canonical, Redacted = safe.ConnectionString };
    }
}

/// <summary>
/// T-SQL-to-MySQL dialect for the SOURCE side: backtick quoting (a literal backtick doubles), the schema part
/// of the three-part name addressing the MySQL database (MySQL schema IS the database), and DATE_SUB
/// arithmetic. Binary watermark literals use the 0x hex form MySQL shares with SQL Server.
/// </summary>
public sealed class MySqlSourceDialect : ISourceSqlDialect
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public string QuoteIdentifier(string identifier)
        => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    public string QualifyObject(RelationalObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
    }

    public string DateSubtractDays(string expression, int days) => $"DATE_SUB({expression}, INTERVAL {days} DAY)";

    public string FormatBinaryLiteral(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "0x" + Convert.ToHexString(value);
    }

    // MySQL accepts an ISO-8601 date/time string literal directly in a comparison.
    public string FormatTemporalLiteral(string baseType, string isoBody)
    {
        ArgumentNullException.ThrowIfNull(isoBody);
        return $"'{isoBody}'";
    }
}
