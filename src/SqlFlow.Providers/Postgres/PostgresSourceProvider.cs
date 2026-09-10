using System.Data.Common;
using Npgsql;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Providers.Postgres;

/// <summary>Opens pooled PostgreSQL connections (Npgsql).</summary>
public sealed class PostgresProviderConnectionFactory : IProviderConnectionFactory
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.PostgreSQL;

    public async Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var npgsqlConnection = new NpgsqlConnection(connection.CanonicalString);
        try
        {
            await npgsqlConnection.OpenAsync(ct).ConfigureAwait(false);
            return npgsqlConnection;
        }
        catch
        {
            await npgsqlConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Canonicalizes PostgreSQL connection strings through <see cref="NpgsqlConnectionStringBuilder"/> and
/// enforces the secretless gate. PostgreSQL has no self-authenticating connection-string mode in this engine,
/// so a literal inline string can never satisfy <see cref="SecretlessPolicy.RequireSelfAuthenticating"/>: a
/// PostgreSQL connection must arrive as a whole <c>${...}</c> secret reference or through a registry entry
/// whose credential mode trusts the value.
/// </summary>
public sealed class PostgresConnectionStringCanonicalizer : IConnectionStringCanonicalizer
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.PostgreSQL;

    public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Malformed PostgreSQL connection string: {ex.Message}", ex);
        }

        switch (policy)
        {
            case SecretlessPolicy.Trusted:
                break; // expanded from a whole ${...} secret reference
            case SecretlessPolicy.RejectRestingSecret when string.IsNullOrEmpty(builder.Password):
                break;
            case SecretlessPolicy.RejectRestingSecret:
                throw new SqlFlowException(
                    "PostgreSQL connection strings must not contain a resting password. Supply the whole string as a ${...} secret reference.");
            default:
                throw new SqlFlowException(
                    "PostgreSQL has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.");
        }

        builder.ApplicationName = role == ConnectionRole.Source ? "SQLFlow Source" : "SQLFlow Target";
        var canonical = builder.ConnectionString;

        var safe = new NpgsqlConnectionStringBuilder(canonical) { Password = string.Empty, Username = string.Empty };
        return new CanonicalConnection { Canonical = canonical, Redacted = safe.ConnectionString };
    }
}

/// <summary>
/// The PostgreSQL source dialect: double-quote quoting (a literal quote doubles; quoting also preserves the
/// case of introspected identifiers, which unquoted PostgreSQL would fold to lowercase), schema.name
/// qualification on the connection's database, interval day arithmetic, and bytea hex literals.
/// </summary>
public sealed class PostgresSourceDialect : ISourceSqlDialect
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.PostgreSQL;

    public string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public string QualifyObject(RelationalObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
    }

    public string DateSubtractDays(string expression, int days) => $"({expression} - INTERVAL '{days} days')";

    public string FormatBinaryLiteral(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"'\\x{Convert.ToHexString(value)}'::bytea";
    }

    // PostgreSQL parses an ISO-8601 date/time string literal directly in a comparison.
    public string FormatTemporalLiteral(string baseType, string isoBody)
    {
        ArgumentNullException.ThrowIfNull(isoBody);
        return $"'{isoBody}'";
    }
}
