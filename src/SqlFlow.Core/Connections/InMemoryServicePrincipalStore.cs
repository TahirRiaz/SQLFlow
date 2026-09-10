using System.Text.RegularExpressions;

namespace SqlFlow.Core.Connections;

/// <summary>
/// A service-principal registry held entirely in memory, built from a YAML document's
/// <c>servicePrincipals:</c> block (or any other in-process source). Reporting <see cref="SupportsAliases"/> as
/// true is what lets the Azure invoke resolver work WITHOUT a control database: same resolver, same credential
/// composition, just a dictionary behind the seam instead of flw.ServicePrincipal. The secretless gate runs at
/// registration: the only secret-bearing field, <see cref="ServicePrincipalProfile.ClientSecretRef"/>, must be
/// a whole <c>${scheme:locator}</c> reference or null (null means the ambient Azure credential), the same rule
/// the SQL store enforces on read.
/// </summary>
public sealed partial class InMemoryServicePrincipalStore : IServicePrincipalStore
{
    private readonly Dictionary<string, ServicePrincipalProfile> _profiles;

    public InMemoryServicePrincipalStore(IEnumerable<ServicePrincipalProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = new Dictionary<string, ServicePrincipalProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile.ClientSecretRef is not null && !WholeReference().IsMatch(profile.ClientSecretRef))
            {
                throw new SecretlessViolationException(profile.Alias, "ClientSecretRef");
            }

            if (!_profiles.TryAdd(profile.Alias, profile))
            {
                throw new SqlFlowException($"Duplicate service-principal name '{profile.Alias}'.");
            }
        }
    }

    public bool SupportsAliases => true;

    public Task<ServicePrincipalProfile> ResolveAsync(string aliasName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);
        return _profiles.TryGetValue(aliasName, out var profile)
            ? Task.FromResult(profile)
            : throw new ServicePrincipalNotFoundException(aliasName);
    }

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();
}
