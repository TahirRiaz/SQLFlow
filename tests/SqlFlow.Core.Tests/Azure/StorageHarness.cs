using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Tests.Azure;

/// <summary>Builds <see cref="AzureStorageAccess"/> over a <see cref="FakeStorageAccount"/>, with a fake DNS and token.</summary>
internal static class StorageHarness
{
    internal const string PublicAddress = "20.60.1.1";
    internal const string PrivateAddress = "10.1.2.3";

    /// <summary>
    /// Access to <paramref name="account"/>. Both host names resolve to a public address unless
    /// <paramref name="privateHosts"/> names one, which then resolves to a private address. Retries wait a millisecond.
    /// </summary>
    internal static AzureStorageAccess Access(
        FakeStorageAccount account,
        string? endpoint = null,
        string[]? privateHosts = null,
        IAzureCredentialFactory? credentials = null,
        ILogger? logger = null)
        => new(
            credentials ?? new FakeCredentials(),
            new AzureStorageAccessOptions
            {
                EndpointSetting = endpoint,
                Transport = account.Transport,
                ResolveHost = (host, _) => Task.FromResult(new[]
                {
                    IPAddress.Parse(privateHosts is not null && privateHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ? PrivateAddress : PublicAddress),
                }),
                RetryDelay = TimeSpan.FromMilliseconds(1),
                ProbeTimeout = TimeSpan.FromSeconds(5),
            },
            logger);

    internal static AzureBlobLocation Location(FakeStorageAccount account, string container, string path = "")
        => AzureBlobLocation.Parse($"abfss://{container}@{account.Account}.dfs.core.windows.net/{path}");
}

/// <summary>A credential factory whose token is a fixed string, or one whose token fails as a broken login does.</summary>
internal sealed class FakeCredentials(bool fail = false) : IAzureCredentialFactory
{
    public TokenCredential Create() => new Token(fail);

    private sealed class Token(bool fail) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => fail
                ? throw new AuthenticationFailedException("The test credential refuses to sign in.")
                : new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

/// <summary>A logger that keeps every message it is given.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly Lock _gate = new();

    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}

/// <summary>A secret resolver that returns references as their values.</summary>
internal sealed class LiteralSecrets : ISecretResolver
{
    public string Resolve(string value) => value;

    public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(value);
}
