using SqlFlow.Core;

namespace SqlFlow.Core.Connections;

/// <summary>
/// Resolves an <c>@alias</c> to a <see cref="DataSource"/>. Two backends share this one contract so the
/// resolver has a single code path in both modes: <c>NullDataSourceStore</c> (lightweight, no control
/// database) and a SQL-backed store (full mode, one indexed seek). Implementations cache only immutable
/// <see cref="DataSource"/> records, never a live connection.
/// </summary>
public interface IDataSourceStore
{
    /// <summary>True only in full mode. Lets the resolver give a precise error for an <c>@alias</c> used in
    /// lightweight mode before it attempts a lookup.</summary>
    bool SupportsAliases { get; }

    /// <summary>Resolves a registered alias. Throws <see cref="SqlFlowException"/> for an unknown alias;
    /// never returns a null or empty connection.</summary>
    Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default);
}
