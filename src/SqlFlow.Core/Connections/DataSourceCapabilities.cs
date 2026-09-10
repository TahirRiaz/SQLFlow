namespace SqlFlow.Core.Connections;

/// <summary>
/// Capability metadata that drives code generation. Every flag is a non-null bool with a safe default, so
/// generators never branch on an unknown tri-state. Mirrors the legacy <c>flw.SysDataSource</c> bit columns
/// (all of which are nullable in the legacy schema and are coalesced to false on migration).
/// </summary>
public sealed record DataSourceCapabilities
{
    /// <summary>Legacy <c>IsSynapse</c>: gates Synapse-incompatible T-SQL (for example disabling and
    /// rebuilding nonclustered indexes) and locking-hint choices in generated SQL.</summary>
    public bool IsSynapse { get; init; }

    /// <summary>Legacy <c>SupportsCrossDBRef</c>: allows three-part names in generated SQL.</summary>
    public bool SupportsCrossDbRef { get; init; }

    /// <summary>Legacy <c>IsLocal</c>: the data source is on the same server as SQLFlow, enabling a
    /// same-server cross-database move instead of a network bulk copy.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Legacy <c>ActivityMonitoring</c>: enables per-connection DMV polling for this source.</summary>
    public bool ActivityMonitoring { get; init; }
}
