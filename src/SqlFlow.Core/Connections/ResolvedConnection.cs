namespace SqlFlow.Core.Connections;

/// <summary>
/// The single output of connection resolution, produced once per run and passed to every provider. It
/// carries the openable <see cref="CanonicalString"/> (treated as a secret) and the always-safe-to-log
/// <see cref="RedactedString"/>, plus the capability and credential plan. The canonical string is the
/// ADO.NET pool key; only the redacted string is ever logged, traced, or put into an exception message.
/// </summary>
public sealed record ResolvedConnection
{
    /// <summary>The openable, canonicalized connection string (the pool key). Secret: never logged,
    /// traced, or included in an exception message.</summary>
    public required string CanonicalString { get; init; }

    /// <summary>The connection string with the secret-bearing keywords removed. Safe to log.</summary>
    public required string RedactedString { get; init; }

    public required DataSourceKind Kind { get; init; }

    public DataSourceCapabilities Capabilities { get; init; } = new();

    public CredentialProfile Credential { get; init; } = new();

    public StorageContext Storage { get; init; } = new();

    /// <summary>Returns the redacted string, so an accidental interpolation of this object into a log or
    /// exception cannot leak the openable connection string.</summary>
    public override string ToString() => RedactedString;
}
