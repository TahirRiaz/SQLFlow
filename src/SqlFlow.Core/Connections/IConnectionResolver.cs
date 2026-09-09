namespace SqlFlow.Core.Connections;

/// <summary>Whether a connection is being resolved for a flow's source or its target. Stamps the
/// <c>Application Name</c> on the canonical string ("SQLFlow Source" / "SQLFlow Target").</summary>
public enum ConnectionRole
{
    Source,
    Target,
}

/// <summary>
/// The single way to turn a raw connection reference into a <see cref="ResolvedConnection"/>. One pipeline
/// serves both modes and both roles: classify the reference, resolve an <c>@alias</c> through the
/// <see cref="IDataSourceStore"/> (full mode only), expand <c>${...}</c> secret references, then canonicalize
/// and apply the secretless gate. The same instance is reused; it holds no per-call state.
/// </summary>
public interface IConnectionResolver
{
    /// <summary>Resolves a reference. <paramref name="inlineKind"/> sets the provider of an INLINE reference
    /// (a connection string or a whole <c>${...}</c> reference), which otherwise defaults to SQL Server; an
    /// <c>@alias</c> reference always takes its kind from the registry and ignores it.</summary>
    Task<ResolvedConnection> ResolveAsync(string rawReference, ConnectionRole role, DataSourceKind? inlineKind = null, CancellationToken ct = default);
}
