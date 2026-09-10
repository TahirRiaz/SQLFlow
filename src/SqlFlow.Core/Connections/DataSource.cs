namespace SqlFlow.Core.Connections;

/// <summary>
/// The kind of relational system a data source is: the three legacy <c>flw.SysDataSource.SourceType</c>
/// values plus PostgreSQL and Oracle (new in V3). MySQL, PostgreSQL, and Oracle are SOURCE-only kinds; the
/// target of every flow is always SQL Server. There is deliberately no Synapse value here; Synapse-ness is a
/// capability (<see cref="DataSourceCapabilities.IsSynapse"/>), not a kind, so the two can never disagree.
/// </summary>
public enum DataSourceKind
{
    MSSQL,
    AZDB,
    MySQL,
    PostgreSQL,
    Oracle,
}

/// <summary>
/// A registry entry: the V3 equivalent of one <c>flw.SysDataSource</c> row joined to its credential
/// profile. Immutable, so the store can safely cache it by alias. It holds only references, never a secret:
/// <see cref="ConnectionRef"/> is an inline secretless connection string or a <c>${...}</c> reference.
/// </summary>
public sealed record DataSource
{
    /// <summary>The alias key (legacy <c>Alias</c>); the text after '@' in an <c>@alias</c> reference.</summary>
    public required string Alias { get; init; }

    /// <summary>The relational kind (legacy <c>SourceType</c>).</summary>
    public required DataSourceKind Kind { get; init; }

    /// <summary>Host or server, for display and diagnostics only (legacy <c>DatabaseName</c>, a misnomer).</summary>
    public string? Host { get; init; }

    /// <summary>
    /// An inline secretless connection string or a <c>${...}</c> secret reference. Never a raw secret
    /// (legacy <c>ConnectionString</c>, converted to a reference or a passwordless string).
    /// </summary>
    public required string ConnectionRef { get; init; }

    public DataSourceCapabilities Capabilities { get; init; } = new();

    public CredentialProfile Credential { get; init; } = new();

    public StorageContext Storage { get; init; } = new();
}
