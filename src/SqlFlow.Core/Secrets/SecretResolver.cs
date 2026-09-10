using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Secrets;

/// <summary>
/// Expands <c>${scheme:locator}</c> references using the registered <see cref="ISecretProvider"/>s.
/// Secrets are never stored in pipeline YAML - values carry references (e.g. <c>${env:SQLFLOW_DW}</c>
/// or <c>${keyvault:my-vault/dw-conn}</c>) resolved at runtime.
/// </summary>
public sealed partial class SecretResolver : ISecretResolver
{
    private readonly IReadOnlyDictionary<string, ISecretProvider> _providers;

    public SecretResolver(IEnumerable<ISecretProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    public string Resolve(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Reference().Replace(value, match =>
        {
            var scheme = match.Groups["scheme"].Value;
            var locator = match.Groups["locator"].Value;

            if (!_providers.TryGetValue(scheme, out var provider))
            {
                throw new SqlFlowException($"No secret provider is registered for scheme '{scheme}' (referenced by '{value}').");
            }

            return provider.Resolve(locator);
        });
    }

    public async Task<string> ResolveAsync(string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var matches = Reference().Matches(value);
        if (matches.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            builder.Append(value, cursor, match.Index - cursor);

            var scheme = match.Groups["scheme"].Value;
            var locator = match.Groups["locator"].Value;
            if (!_providers.TryGetValue(scheme, out var provider))
            {
                throw new SqlFlowException($"No secret provider is registered for scheme '{scheme}' (referenced by '{value}').");
            }

            builder.Append(await provider.ResolveAsync(locator, ct).ConfigureAwait(false));
            cursor = match.Index + match.Length;
        }

        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    [GeneratedRegex(@"\$\{(?<scheme>[a-zA-Z]+):(?<locator>[^}]+)\}")]
    private static partial Regex Reference();
}
