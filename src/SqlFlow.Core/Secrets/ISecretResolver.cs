namespace SqlFlow.Core.Secrets;

/// <summary>
/// Expands secret references of the form <c>${scheme:locator}</c> in a string (e.g. a connection
/// string), dispatching each to the matching <see cref="ISecretProvider"/>.
/// </summary>
public interface ISecretResolver
{
    string Resolve(string value);

    /// <summary>
    /// Asynchronously expands the <c>${scheme:locator}</c> references in <paramref name="value"/>. Used on
    /// the connection-resolution path so a network-bound provider (for example Key Vault) does not block a
    /// thread-pool thread.
    /// </summary>
    Task<string> ResolveAsync(string value, CancellationToken ct = default);
}
