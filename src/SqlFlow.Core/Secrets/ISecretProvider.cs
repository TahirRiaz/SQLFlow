namespace SqlFlow.Core.Secrets;

/// <summary>
/// Resolves a secret reference for one scheme. Plugged in per backend - environment variables,
/// Azure Key Vault, AWS Secrets Manager, GCP Secret Manager - so the same reference syntax works
/// everywhere. Reusable across the engine (connections, storage credentials, API keys, …).
/// </summary>
public interface ISecretProvider
{
    /// <summary>The reference scheme this provider handles, e.g. "env", "keyvault".</summary>
    string Scheme { get; }

    /// <summary>Resolves the locator (the part after the scheme) to a secret value.</summary>
    string Resolve(string locator);

    /// <summary>
    /// Asynchronously resolves the locator to a secret value. The default wraps the synchronous
    /// <see cref="Resolve"/>; a network-bound provider (for example Key Vault) overrides it with a
    /// genuinely asynchronous implementation.
    /// </summary>
    Task<string> ResolveAsync(string locator, CancellationToken ct = default) => Task.FromResult(Resolve(locator));
}
