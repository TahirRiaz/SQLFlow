using SqlFlow.Core;

namespace SqlFlow.Core.Connections;

/// <summary>
/// Resolves an Azure service-principal <c>@alias</c> to a <see cref="ServicePrincipalProfile"/>. Mirrors
/// <see cref="IDataSourceStore"/>: two backends share one contract so a resolver has a single code path in both
/// modes. <see cref="NullServicePrincipalStore"/> is the lightweight (no control database) default; a SQL-backed
/// store is full mode. Implementations cache only the immutable profile, never a credential.
/// </summary>
public interface IServicePrincipalStore
{
    /// <summary>True only in full mode. Lets a resolver give a precise error for an <c>@alias</c> used in
    /// lightweight mode before it attempts a lookup.</summary>
    bool SupportsAliases { get; }

    /// <summary>Resolves a registered service-principal alias. Throws
    /// <see cref="ServicePrincipalNotFoundException"/> for an unknown alias.</summary>
    Task<ServicePrincipalProfile> ResolveAsync(string aliasName, CancellationToken ct = default);
}

/// <summary>
/// The lightweight-mode service-principal store: there is no control database, so no alias can be resolved. It
/// is the default so a resolver shares one code path in both modes; a caller checks <see cref="SupportsAliases"/>
/// first and gives a precise error, so <see cref="ResolveAsync"/> is reached only if called directly, where it
/// throws the same clear error.
/// </summary>
public sealed class NullServicePrincipalStore : IServicePrincipalStore
{
    public static readonly NullServicePrincipalStore Instance = new();

    private NullServicePrincipalStore()
    {
    }

    public bool SupportsAliases => false;

    public Task<ServicePrincipalProfile> ResolveAsync(string aliasName, CancellationToken ct = default)
        => throw new SqlFlowException(
            $"Service-principal alias '@{aliasName}' cannot be resolved in lightweight mode. Configure a control " +
            "database (full mode) with an flw.ServicePrincipal registry to trigger Azure Data Factory / Automation.");
}
