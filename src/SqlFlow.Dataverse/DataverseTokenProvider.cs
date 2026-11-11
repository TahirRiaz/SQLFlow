using Azure.Core;
using Azure.Identity;
using SqlFlow.Core;

namespace SqlFlow.Dataverse;

/// <summary>Hands out the bearer token every Dataverse request carries.</summary>
internal interface IDataverseTokenProvider
{
    /// <summary>
    /// A token valid for the environment. <paramref name="forceRefresh"/> asks for a new one rather than the kept one,
    /// after the service refused the last; the identity's own token cache may still answer with the same token when it
    /// has not expired, which then fails the request again and is reported as a refusal.
    /// </summary>
    ValueTask<string> GetTokenAsync(bool forceRefresh, CancellationToken ct);

    /// <summary>Who signs in, for messages: the service principal's client id or the ambient identity's mode. Never a secret.</summary>
    string Identity { get; }
}

/// <summary>
/// A token provider over an Azure <see cref="TokenCredential"/> for one environment scope. The token is kept and reused
/// until five minutes before it expires, so a long read never sends a token that lapses in flight, and a run over many
/// pages asks the identity for a token about once an hour rather than once a page. Two pages asking at the same moment
/// (the page being read and the next one fetched ahead) may both ask the identity; Azure.Identity serves the second from
/// its own cache, so no lock is held across the await.
/// </summary>
internal sealed class DataverseTokenProvider : IDataverseTokenProvider
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _request;
    private readonly TimeProvider _time;
    private readonly string _environment;
    private TokenHolder? _token;

    public DataverseTokenProvider(TokenCredential credential, string scope, string identity, string environment, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentNullException.ThrowIfNull(time);
        _credential = credential;
        _request = new TokenRequestContext([scope]);
        Identity = identity;
        _environment = environment;
        _time = time;
    }

    public string Identity { get; }

    public async ValueTask<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var kept = Volatile.Read(ref _token);
        if (!forceRefresh && kept is not null && kept.Value.ExpiresOn - RefreshAhead > _time.GetUtcNow())
        {
            return kept.Value.Token;
        }

        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(_request, ct).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex)
        {
            // Azure.Identity's messages name the identity and the reason; they never carry the secret.
            throw new SqlFlowException(
                $"Signing in to the Dataverse environment {_environment} as {Identity} failed: {ex.Message}", ex);
        }

        Volatile.Write(ref _token, new TokenHolder(token));
        return token.Token;
    }

    /// <summary>A reference wrapper, so the kept token is swapped atomically.</summary>
    private sealed record TokenHolder(AccessToken Value);
}
