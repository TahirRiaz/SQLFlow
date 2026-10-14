using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Secrets;

/// <summary>
/// Expands <c>${scheme:locator}</c> references using the registered <see cref="ISecretProvider"/>s.
/// Secrets are never stored in pipeline YAML - values carry references (e.g. <c>${env:SQLFLOW_DW}</c>
/// or <c>${keyvault:my-vault/dw-conn}</c>) resolved at runtime.
/// </summary>
/// <remarks>
/// Inside a scope a run's supplied values opened (<see cref="SuppliedReferences.Supply"/>), an <c>${env:NAME}</c> reference
/// takes its value from them before the process environment, and the value they hold is then expanded by the providers
/// alone, so supplied values never chain into one another.
/// </remarks>
public sealed partial class SecretResolver : ISecretResolver
{
    private const string EnvScheme = "env";

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
            return IsEnv(scheme) && SuppliedReferences.TryTake(locator, out var supplied)
                ? Expand(supplied)
                : Provider(scheme, value).Resolve(locator);
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
            builder.Append(IsEnv(scheme) && SuppliedReferences.TryTake(locator, out var supplied)
                ? await ExpandAsync(supplied, ct).ConfigureAwait(false)
                : await Provider(scheme, value).ResolveAsync(locator, ct).ConfigureAwait(false));
            cursor = match.Index + match.Length;
        }

        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    /// <summary>A supplied value with its own references expanded by the providers alone: supplied values never chain.</summary>
    private string Expand(string supplied)
        => Reference().Replace(supplied, match => Provider(match.Groups["scheme"].Value, supplied).Resolve(match.Groups["locator"].Value));

    private async Task<string> ExpandAsync(string supplied, CancellationToken ct)
    {
        var matches = Reference().Matches(supplied);
        if (matches.Count == 0)
        {
            return supplied;
        }

        var builder = new StringBuilder(supplied.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            builder.Append(supplied, cursor, match.Index - cursor);
            builder.Append(await Provider(match.Groups["scheme"].Value, supplied).ResolveAsync(match.Groups["locator"].Value, ct).ConfigureAwait(false));
            cursor = match.Index + match.Length;
        }

        builder.Append(supplied, cursor, supplied.Length - cursor);
        return builder.ToString();
    }

    private ISecretProvider Provider(string scheme, string value)
        => _providers.TryGetValue(scheme, out var provider)
            ? provider
            : throw new SqlFlowException($"No secret provider is registered for scheme '{scheme}' (referenced by '{value}').");

    private static bool IsEnv(string scheme) => string.Equals(scheme, EnvScheme, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\$\{(?<scheme>[a-zA-Z]+):(?<locator>[^}]+)\}")]
    private static partial Regex Reference();
}
