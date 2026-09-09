using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer;

/// <summary>
/// The SQL Server implementation of the connection-string canonicalizer: it normalizes a connection string
/// through <see cref="SqlConnectionStringBuilder"/> (so synonyms collapse to one stable pool key), stamps the
/// application name by role, produces a redacted form for logging, and enforces the secretless gate by
/// inspecting the parsed builder (never raw text). Handles MSSQL and AZDB.
/// </summary>
public sealed class SqlConnectionStringCanonicalizer : IConnectionStringCanonicalizer
{
    public bool CanHandle(DataSourceKind kind) => kind is DataSourceKind.MSSQL or DataSourceKind.AZDB;

    public CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new SqlFlowException($"Malformed SQL Server connection string: {ex.Message}", ex);
        }

        InspectSecretless(builder, policy);

        builder.ApplicationName = role == ConnectionRole.Source ? "SQLFlow Source" : "SQLFlow Target";
        if (builder.ConnectTimeout == 15)
        {
            builder.ConnectTimeout = 30; // only when left at the provider default
        }

        var canonical = builder.ConnectionString;

        var safe = new SqlConnectionStringBuilder(canonical);
        safe.Remove("Password");
        safe.Remove("Pwd");
        safe.Remove("User ID");
        safe.Remove("UID");

        return new CanonicalConnection { Canonical = canonical, Redacted = safe.ConnectionString };
    }

    private static void InspectSecretless(SqlConnectionStringBuilder builder, SecretlessPolicy policy)
    {
        if (policy == SecretlessPolicy.Trusted)
        {
            return; // expanded from a whole ${...} secret reference; credentials came from a secret store
        }

        // Rule 1 (always, for a non-trusted value): no resting password. Whitespace and casing cannot bypass
        // this because it inspects the parsed Password property, not raw text.
        if (!string.IsNullOrEmpty(builder.Password))
        {
            throw new SqlFlowException(
                "Inline connection strings must not contain a password. Supply the whole string as a ${...} secret reference.");
        }

        if (policy == SecretlessPolicy.RejectRestingSecret)
        {
            return; // a token is injected at open time; the string legitimately omits an auth keyword
        }

        // Rule 2 (RequireSelfAuthenticating): only passwordless Active Directory or Integrated Security.
        var passwordless =
            builder.Authentication is SqlAuthenticationMethod.ActiveDirectoryDefault
                or SqlAuthenticationMethod.ActiveDirectoryManagedIdentity
                or SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity
            || (builder.Authentication == SqlAuthenticationMethod.NotSpecified && builder.IntegratedSecurity);

        if (!passwordless)
        {
            throw new SqlFlowException(
                "Inline connection strings may use only Active Directory Default/Managed Identity/Workload Identity " +
                "or Integrated Security=true. A password-bearing string must be supplied as a whole ${...} secret reference.");
        }
    }
}
