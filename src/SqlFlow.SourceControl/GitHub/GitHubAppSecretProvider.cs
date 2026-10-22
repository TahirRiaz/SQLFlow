using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>
/// Resolves <c>${githubapp:...}</c> references (<see cref="GitHubAppReference"/>) to a GitHub App installation token,
/// minted when it is resolved and reused for a short window by the process's <see cref="GitHubAppTokenClient"/>. It is
/// registered beside <c>env</c> and <c>keyvault</c>, so every place that takes a secret reference (a repository source's
/// git credential, an scm flow's <c>secret</c>) can name a GitHub App instead of a personal access token. What a
/// reference leaves out comes from the host's App (<see cref="GitHubAppEnvironment"/>), read when it is resolved.
/// </summary>
public sealed class GitHubAppSecretProvider : ISecretProvider
{
    private readonly GitHubAppTokenClient _client;
    private readonly Func<string, string?> _environment;

    public GitHubAppSecretProvider(GitHubAppTokenClient client)
        : this(client, Environment.GetEnvironmentVariable)
    {
    }

    internal GitHubAppSecretProvider(GitHubAppTokenClient client, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        _client = client;
        _environment = environment;
    }

    public string Scheme => GitHubAppReference.Scheme;

    /// <summary>The token, minted synchronously. Callers on an asynchronous path use <see cref="ResolveAsync"/>.</summary>
    public string Resolve(string locator) => ResolveAsync(locator).GetAwaiter().GetResult();

    /// <exception cref="GitHubAppException">The reference is malformed, the host's App is misconfigured, or GitHub
    /// refused or did not answer; the message names what to change.</exception>
    public async Task<string> ResolveAsync(string locator, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var request = GitHubAppReference.Parse(locator, GitHubAppEnvironment.Read(_environment));
        var token = await _client.GetTokenAsync(request, ct).ConfigureAwait(false);
        return token.Token;
    }
}
