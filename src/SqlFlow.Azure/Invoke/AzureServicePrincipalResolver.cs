using Azure.Core;
using Azure.Identity;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Azure.Invoke;

/// <summary>The credential and resource coordinates an Azure invoke executor needs, resolved from a secretless
/// service-principal alias.</summary>
public sealed record ResolvedServicePrincipal
{
    public required TokenCredential Credential { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceGroup { get; init; }

    public string? DataFactoryName { get; init; }

    public string? AutomationAccountName { get; init; }
}

/// <summary>
/// Turns an <c>@alias</c> service-principal reference (from the invoke flow) into a usable
/// <see cref="TokenCredential"/> plus the ARM coordinates. This is the single code path both Azure executors use.
/// The client secret is expanded transiently from its <c>${...}</c> reference through the shared
/// <see cref="ISecretResolver"/>; when the profile carries no secret reference the deployment's ambient
/// credential (managed identity / Azure CLI / env default) is used instead, so no secret need rest anywhere.
/// </summary>
public sealed class AzureServicePrincipalResolver
{
    private readonly IServicePrincipalStore _store;
    private readonly ISecretResolver _secrets;
    private readonly IAzureCredentialFactory _credentialFactory;

    public AzureServicePrincipalResolver(IServicePrincipalStore store, ISecretResolver secrets, IAzureCredentialFactory credentialFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _store = store;
        _secrets = secrets;
        _credentialFactory = credentialFactory;
    }

    public async Task<ResolvedServicePrincipal> ResolveAsync(string? reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new SqlFlowException(
                "This invoke targets Azure but has no service principal: set trgServicePrincipalAlias on the flw.Invoke flow.");
        }

        var aliasRef = ConnectionRef.Parse(reference);
        if (aliasRef.Kind != ConnectionRefKind.Alias)
        {
            throw new SqlFlowException($"Service-principal reference '{reference}' must be an '@alias'.");
        }

        if (!_store.SupportsAliases)
        {
            throw new SqlFlowException(
                $"Service-principal alias '@{aliasRef.Value}' requires full mode (a control database with an flw.ServicePrincipal registry).");
        }

        var profile = await _store.ResolveAsync(aliasRef.Value, ct).ConfigureAwait(false);
        var subscriptionId = Require(profile.SubscriptionId, profile.Alias, "SubscriptionId");
        var resourceGroup = Require(profile.ResourceGroup, profile.Alias, "ResourceGroup");
        var credential = await BuildCredentialAsync(profile, ct).ConfigureAwait(false);

        return new ResolvedServicePrincipal
        {
            Credential = credential,
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            DataFactoryName = profile.DataFactoryName,
            AutomationAccountName = profile.AutomationAccountName,
        };
    }

    private async Task<TokenCredential> BuildCredentialAsync(ServicePrincipalProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ClientSecretRef))
        {
            // No explicit secret reference: authenticate as the deployment's ambient identity.
            return _credentialFactory.Create();
        }

        var tenantId = Require(profile.TenantId, profile.Alias, "TenantId");
        var clientId = Require(profile.ClientId, profile.Alias, "ClientId");
        var clientSecret = await _secrets.ResolveAsync(profile.ClientSecretRef, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new SqlFlowException(
                $"Service principal '{profile.Alias}' resolved an empty client secret from '{profile.ClientSecretRef}'.");
        }

        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    private static string Require(string? value, string alias, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new SqlFlowException($"Service principal '{alias}' is missing the required '{field}'.")
            : value;
}
