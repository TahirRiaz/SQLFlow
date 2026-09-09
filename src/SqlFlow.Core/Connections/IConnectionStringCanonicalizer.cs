namespace SqlFlow.Core.Connections;

/// <summary>
/// The secretless gate applied while canonicalizing an inline connection string. The resolver chooses the
/// policy from the reference shape and the credential mode; the canonicalizer enforces it.
/// </summary>
public enum SecretlessPolicy
{
    /// <summary>Full gate: reject any resting password, and require a passwordless self-authenticating
    /// string (Active Directory Default / Managed Identity / Workload Identity, or Integrated Security).
    /// Applied to a literal inline string that must authenticate itself.</summary>
    RequireSelfAuthenticating,

    /// <summary>Reject a resting password only. The string legitimately omits an auth keyword because a
    /// token is injected at open time (the cross-tenant <see cref="CredentialMode.InjectedToken"/> case).</summary>
    RejectRestingSecret,

    /// <summary>No gate. The value was expanded from a whole <c>${...}</c> secret reference, so any
    /// credentials it carries came from a secret store at runtime and are not at rest in YAML or the
    /// control database.</summary>
    Trusted,
}

/// <summary>The canonicalized connection: the openable string (the pool key) and a redacted form safe to log.</summary>
public sealed record CanonicalConnection
{
    public required string Canonical { get; init; }
    public required string Redacted { get; init; }
}

/// <summary>
/// Normalizes a connection string into a stable canonical form (the ADO.NET pool key) and a redacted form,
/// and enforces the secretless gate per <see cref="SecretlessPolicy"/>. This is provider-specific (SQL
/// Server uses <c>SqlConnectionStringBuilder</c>, MySQL uses <c>MySqlConnectionStringBuilder</c>), so the
/// implementations live in the provider assemblies; the pure-Core <see cref="IConnectionResolver"/> depends
/// only on this abstraction, which keeps <c>Microsoft.Data.SqlClient</c> out of Core.
/// </summary>
public interface IConnectionStringCanonicalizer
{
    /// <summary>True if this canonicalizer handles the given <see cref="DataSourceKind"/>.</summary>
    bool CanHandle(DataSourceKind kind);

    /// <summary>Canonicalizes and redacts the connection string, throwing on a secretless-policy violation.</summary>
    CanonicalConnection Canonicalize(string connectionString, ConnectionRole role, SecretlessPolicy policy);
}
