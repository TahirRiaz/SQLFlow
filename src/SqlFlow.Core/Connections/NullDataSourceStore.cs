using SqlFlow.Core;

namespace SqlFlow.Core.Connections;

/// <summary>
/// The lightweight-mode data-source store: there is no control database, so no alias can be resolved. It is
/// registered by default so the resolver shares one code path in both modes; the resolver checks
/// <see cref="SupportsAliases"/> first and gives a precise error, so <see cref="ResolveAsync"/> is reached
/// only if the store is called directly, where it throws the same clear error.
/// </summary>
public sealed class NullDataSourceStore : IDataSourceStore
{
    public bool SupportsAliases => false;

    public Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
        => throw new SqlFlowException(
            $"Connection alias '@{aliasName}' cannot be resolved in lightweight mode. Configure a control database " +
            "(full mode) to use connection aliases, or supply an inline connection string or a ${...} secret reference.");
}
