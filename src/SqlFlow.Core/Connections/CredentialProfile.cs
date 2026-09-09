namespace SqlFlow.Core.Connections;

/// <summary>How a resolved connection authenticates.</summary>
public enum CredentialMode
{
    /// <summary>A passwordless <c>Authentication=Active Directory *</c> keyword lives in the connection
    /// string and the driver owns the token. The default and the only pooling-friendly path.</summary>
    ConnectionStringAuth,

    /// <summary><c>Integrated Security=true</c> (on-premises domain). No token, no secret.</summary>
    Integrated,

    /// <summary>A full, already-secret-resolved connection string supplied as a whole <c>${...}</c>
    /// reference (for example legacy SQL authentication whose password lives in Key Vault).</summary>
    InlineConnectionString,

    /// <summary>A per-alias token is acquired for a foreign-tenant service principal and set on the
    /// connection at open time; the connection string itself carries no auth keyword and no secret.</summary>
    InjectedToken,
}

/// <summary>
/// The downstream / cross-tenant service-principal binding for a data source. Empty for the common
/// passwordless case. Any secret is a locator (a <c>${keyvault:...}</c> reference) resolved transiently at
/// runtime, never the secret value itself.
/// </summary>
public sealed record CredentialProfile
{
    public CredentialMode Mode { get; init; } = CredentialMode.ConnectionStringAuth;

    /// <summary>Foreign tenant id (legacy <c>TenantId</c>); used only by <see cref="CredentialMode.InjectedToken"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Application (client) id (legacy <c>ApplicationId</c>); used only by <see cref="CredentialMode.InjectedToken"/>.</summary>
    public string? ClientId { get; init; }

    /// <summary>A <c>${keyvault:...}</c> reference to the client secret. Resolved transiently; never stored.</summary>
    public string? ClientSecretRef { get; init; }

    /// <summary>Key Vault name (legacy <c>KeyVaultName</c>); a locator only.</summary>
    public string? KeyVaultName { get; init; }

    /// <summary>A full vault URI that bypasses suffix guessing (sovereign cloud or private DNS).</summary>
    public string? KeyVaultUriOverride { get; init; }
}
