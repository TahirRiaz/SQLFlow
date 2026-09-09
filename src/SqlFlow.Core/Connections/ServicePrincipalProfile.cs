namespace SqlFlow.Core.Connections;

/// <summary>
/// A secretless Azure service-principal binding (the V3 equivalent of one legacy flw.SysServicePrincipal row,
/// which rested the client secret in plaintext). Immutable, so a store can cache it by alias. Plain coordinates
/// (tenant, client, subscription, resource group, the Data Factory / Automation Account names) rest as literal
/// text; the client secret rests ONLY as a <c>${keyvault:...}</c> / <c>${env:...}</c> reference resolved
/// transiently at runtime, never the value itself. When no secret reference is present the deployment's ambient
/// credential (managed identity / az login) is used instead, which is the preferred production posture.
/// </summary>
public sealed record ServicePrincipalProfile
{
    /// <summary>The unique alias (legacy ServicePrincipalAlias) that an invoke flow references via
    /// <c>@alias</c>.</summary>
    public required string Alias { get; init; }

    public string? TenantId { get; init; }                   // coordinate (Azure AD tenant)

    /// <summary>The application / client id (legacy ApplicationId), a coordinate, not a secret.</summary>
    public string? ClientId { get; init; }

    /// <summary>A <c>${...}</c> reference to the client secret. Resolved transiently; the value never rests
    /// here. Null means "use the ambient credential" (managed identity / Azure CLI / env-based default).</summary>
    public string? ClientSecretRef { get; init; }

    public string? SubscriptionId { get; init; }             // coordinate (ARM context)

    public string? ResourceGroup { get; init; }              // coordinate

    /// <summary>The Data Factory instance name (legacy DataFactoryName), used by the ADF executor.</summary>
    public string? DataFactoryName { get; init; }

    /// <summary>The Automation account name (legacy AutomationAccountName), used by the Automation executor.</summary>
    public string? AutomationAccountName { get; init; }

    /// <summary>The Key Vault that backs <see cref="ClientSecretRef"/>, a coordinate / hint (not a secret).</summary>
    public string? KeyVaultName { get; init; }
}
