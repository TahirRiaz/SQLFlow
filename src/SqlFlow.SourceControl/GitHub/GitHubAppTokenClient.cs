using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>
/// An installation access token of a GitHub App: what git sends as its password (with the user name
/// <c>x-access-token</c>) and the GitHub API takes as a bearer token. <see cref="ToString"/> never shows the token.
/// </summary>
public sealed class GitHubAppToken
{
    internal GitHubAppToken(
        string token, DateTimeOffset expiresUtc, long installationId, GitHubRepositoryName? repository,
        IReadOnlyDictionary<string, string> permissions, string? repositorySelection)
    {
        Token = token;
        ExpiresUtc = expiresUtc;
        InstallationId = installationId;
        Repository = repository;
        Permissions = permissions;
        RepositorySelection = repositorySelection;
    }

    /// <summary>The token. Held in memory for the operation it was minted for; never logged or stored.</summary>
    public string Token { get; }

    /// <summary>When the token expires, on this host's clock (GitHub's answer corrected by the clocks' difference).</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>The installation the token was minted on.</summary>
    public long InstallationId { get; }

    /// <summary>The one repository the token reaches, or null when it reaches every repository of the installation.</summary>
    public GitHubRepositoryName? Repository { get; }

    /// <summary>The permissions GitHub granted the token.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; }

    /// <summary>GitHub's <c>repository_selection</c> for the token: <c>all</c> or <c>selected</c>.</summary>
    public string? RepositorySelection { get; }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"GitHub App installation token (installation {InstallationId}, expires {ExpiresUtc:O})");
}

/// <summary>What GitHub says about an App: its name and owner, and where it is installed.</summary>
/// <param name="Name">The App's name.</param>
/// <param name="Slug">The App's URL slug.</param>
/// <param name="Owner">The account owning the App.</param>
/// <param name="HtmlUrl">The App's page on GitHub.</param>
/// <param name="Installations">The App's installations, at most <see cref="GitHubAppTokenClient.MaxInstallationsListed"/>.</param>
/// <param name="MoreInstallations">Whether the App has more installations than are listed.</param>
public sealed record GitHubAppDetails(
    string Name, string Slug, string? Owner, string? HtmlUrl, IReadOnlyList<GitHubAppInstallation> Installations, bool MoreInstallations);

/// <summary>One installation of an App.</summary>
/// <param name="Id">The installation's id, the one a <c>${githubapp:installation=...}</c> reference names.</param>
/// <param name="Account">The user or organization the App is installed on.</param>
/// <param name="AccountType"><c>Organization</c> or <c>User</c>.</param>
/// <param name="RepositorySelection"><c>all</c> or <c>selected</c>.</param>
/// <param name="SuspendedUtc">When the installation was suspended, or null when it is not.</param>
public sealed record GitHubAppInstallation(
    long Id, string Account, string? AccountType, string? RepositorySelection, DateTimeOffset? SuspendedUtc);

/// <summary>How the client waits, retries and caches. The defaults are production's; the test suite shortens them.</summary>
internal sealed record GitHubAppClientOptions
{
    /// <summary>How long one HTTP attempt may take before it is abandoned and retried.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many attempts a request gets when GitHub is unavailable, slow or rate limiting.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The longest wait between attempts; a rate limit asking for longer fails at once, saying when to retry.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The wait before the second attempt; it doubles for each attempt after.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a minted token is handed out again. Short, so a change to the App's permissions or
    /// installation is seen soon, while an operation never mints more than one token per key in the window.</summary>
    public TimeSpan ReuseFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The least life a handed-out token has left, so a long clone or push never outlives its token.</summary>
    public TimeSpan MinimumRemaining { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the installation found for a repository is remembered.</summary>
    public TimeSpan InstallationLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long "the App is not installed on this repository" is remembered, so a public repository the App
    /// does not reach costs one lookup per window rather than one per sync.</summary>
    public TimeSpan NotInstalledLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The largest response body read; GitHub's answers here are a few kilobytes.</summary>
    public int MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>The clocks' difference above which a refused JSON web token is signed again with GitHub's time.</summary>
    public TimeSpan SkewTolerance { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How the client waits; null waits on the client's clock.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>
/// Mints installation access tokens for GitHub Apps (<c>POST /app/installations/{id}/access_tokens</c>, authenticated
/// with a JSON web token the App's private key signs), and reads an App's own details for a status page. One instance
/// serves a process: it reuses a minted token for a short window (<see cref="GitHubAppClientOptions.ReuseFor"/>) while it
/// has ample life left, lets one caller mint per token while concurrent callers wait for it, remembers which installation
/// holds a repository, retries what GitHub may answer differently a moment later (a 5xx, a timeout, a network error, a
/// rate limit with a short wait), and corrects for a host clock that differs from GitHub's.
/// <para>
/// Nothing secret leaves it except the token itself, returned to the caller: messages and logs name the App, the
/// installation or repository, the API and GitHub's request id, never the private key, a JSON web token or a token.
/// A failed request is never cached, so the next call asks GitHub again.
/// </para>
/// </summary>
public sealed partial class GitHubAppTokenClient : IDisposable
{
    /// <summary>The most installations <see cref="GetAppAsync"/> lists.</summary>
    public const int MaxInstallationsListed = 100;

    private const string UserAgent = "SQLFlow-GitHubApp";
    private const string ApiVersion = "2022-11-28";
    private const int MaxRedirects = 3;
    private const int MaxMessageLength = 300;

    private readonly Func<ISecretResolver> _resolver;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly GitHubAppClientOptions _options;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedInstallation> _installations = new(StringComparer.Ordinal);

    // One gate per distinct token request. Entries are never removed: their number is bounded by the distinct
    // (App, installation or repository, permissions) combinations a process uses, a handful in practice.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    // GitHub's clock minus this host's, per API root, learned from the Date header of every answer.
    private readonly ConcurrentDictionary<string, TimeSpan> _offsets = new(StringComparer.Ordinal);

    /// <param name="resolver">The secret resolver an App's key reference is read through. A factory, because the
    /// resolver's providers include the <c>githubapp</c> provider that uses this client.</param>
    /// <param name="clock">The clock tokens and JSON web tokens are timed by.</param>
    /// <param name="logger">Where minting and retries are logged.</param>
    public GitHubAppTokenClient(Func<ISecretResolver> resolver, TimeProvider? clock = null, ILogger<GitHubAppTokenClient>? logger = null)
        : this(resolver, clock, logger, handler: null, options: null)
    {
    }

    internal GitHubAppTokenClient(
        Func<ISecretResolver> resolver, TimeProvider? clock, ILogger? logger, HttpMessageHandler? handler, GitHubAppClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new GitHubAppClientOptions();

        // Redirects are followed here, not by the handler: the handler drops the Authorization header on a redirect,
        // and only a redirect within the same API may carry the App's signed token. Connections are recycled so a
        // changed DNS answer for the API is picked up.
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// An installation token for <paramref name="request"/>: one handed out before when it is recent and has ample life
    /// left, otherwise a new one.
    /// </summary>
    /// <exception cref="GitHubAppNotInstalledException">The App has no installation reaching the requested repository.</exception>
    /// <exception cref="GitHubAppException">The key could not be read, or GitHub refused or did not answer; the message says what to change.</exception>
    public async Task<GitHubAppToken> GetTokenAsync(GitHubAppTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstallationId is null && request.Repository is null)
        {
            throw new GitHubAppException(
                $"a token of GitHub App {request.App.AppId} needs an installation or a repository to be minted for; the request names neither.");
        }

        if (TryReuse(request.CacheKey, out var reused))
        {
            return reused;
        }

        var gate = _gates.GetOrAdd(request.CacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A caller that waited for the gate usually finds the token the caller ahead of it minted.
            if (TryReuse(request.CacheKey, out reused))
            {
                return reused;
            }

            var minted = await MintAsync(request, ct).ConfigureAwait(false);
            Remember(request.CacheKey, minted);
            return minted;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// What GitHub says about <paramref name="app"/> (its name, owner and installations), read as the App itself, which
    /// proves its ID and key belong together. Nothing is cached: this is what a status check asks for.
    /// </summary>
    /// <exception cref="GitHubAppException">The key could not be read, or GitHub refused or did not answer.</exception>
    public async Task<GitHubAppDetails> GetAppAsync(GitHubAppIdentity app, string apiRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiRoot);

        using var key = await app.Key.LoadAsync(app.AppId, _resolver(), ct).ConfigureAwait(false);

        var answer = await SendAsync(HttpMethod.Get, $"{apiRoot}/app", body: null, app, key, apiRoot, ct).ConfigureAwait(false);
        if (answer.Status != 200)
        {
            throw Failure(answer, app, apiRoot, $"read GitHub App {app.AppId}");
        }

        string name;
        string slug;
        string? owner;
        string? htmlUrl;
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            name = String(root, "name") ?? app.AppId;
            slug = String(root, "slug") ?? app.AppId;
            owner = root.TryGetProperty("owner", out var ownerElement) && ownerElement.ValueKind == JsonValueKind.Object
                ? String(ownerElement, "login")
                : null;
            htmlUrl = String(root, "html_url");
        }
        catch (JsonException ex)
        {
            throw new GitHubAppException($"GitHub answered the request for GitHub App {app.AppId} with a body that is not JSON ({apiRoot}).", ex);
        }

        var listed = await SendAsync(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"{apiRoot}/app/installations?per_page={MaxInstallationsListed}"),
            body: null, app, key, apiRoot, ct).ConfigureAwait(false);
        if (listed.Status != 200)
        {
            throw Failure(listed, app, apiRoot, $"list the installations of GitHub App {app.AppId}");
        }

        var installations = new List<GitHubAppInstallation>();
        try
        {
            using var document = JsonDocument.Parse(listed.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray().Take(MaxInstallationsListed))
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
                    {
                        continue;
                    }

                    string? account = null;
                    string? accountType = null;
                    if (item.TryGetProperty("account", out var accountElement) && accountElement.ValueKind == JsonValueKind.Object)
                    {
                        account = String(accountElement, "login") ?? String(accountElement, "slug") ?? String(accountElement, "name");
                        accountType = String(accountElement, "type");
                    }

                    installations.Add(new GitHubAppInstallation(
                        id, account ?? "(unknown)", accountType, String(item, "repository_selection"), Date(item, "suspended_at")));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new GitHubAppException($"GitHub answered the installations of GitHub App {app.AppId} with a body that is not JSON ({apiRoot}).", ex);
        }

        var more = listed.Link is { } link && link.Contains("rel=\"next\"", StringComparison.Ordinal);
        return new GitHubAppDetails(name, slug, owner, htmlUrl, installations, more);
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    private bool TryReuse(string key, [NotNullWhen(true)] out GitHubAppToken? token)
    {
        if (_tokens.TryGetValue(key, out var cached))
        {
            if (_clock.GetUtcNow() < cached.ReuseUntil)
            {
                token = cached.Token;
                return true;
            }

            _tokens.TryRemove(new KeyValuePair<string, CachedToken>(key, cached));
        }

        token = null;
        return false;
    }

    private void Remember(string key, GitHubAppToken token)
    {
        var now = _clock.GetUtcNow();
        var until = Min(now + _options.ReuseFor, token.ExpiresUtc - _options.MinimumRemaining);
        if (until > now)
        {
            _tokens[key] = new CachedToken(token, until);
        }
        else
        {
            // GitHub handed out a token with less life than any reuse needs (a host clock far off GitHub's, or a
            // shorter lifetime than GitHub documents): it serves this call alone.
            LogShortLivedToken(_logger, token.InstallationId, token.ExpiresUtc);
        }
    }

    private async Task<GitHubAppToken> MintAsync(GitHubAppTokenRequest request, CancellationToken ct)
    {
        using var key = await request.App.Key.LoadAsync(request.App.AppId, _resolver(), ct).ConfigureAwait(false);

        var found = request.InstallationId is null;
        var installation = request.InstallationId
            ?? await FindInstallationAsync(request, key, refresh: false, ct).ConfigureAwait(false);
        var body = TokenRequestBody(request);

        for (var pass = 0; ; pass++)
        {
            var answer = await SendAsync(
                HttpMethod.Post,
                string.Create(CultureInfo.InvariantCulture, $"{request.ApiRoot}/app/installations/{installation}/access_tokens"),
                body, request.App, key, request.ApiRoot, ct).ConfigureAwait(false);

            if (answer.Status is 200 or 201)
            {
                var token = ReadToken(answer, request, installation);
                LogMinted(_logger, request.App.AppId, installation, request.Repository?.ToString() ?? "(all)", token.ExpiresUtc);
                return token;
            }

            // An installation found earlier and remembered may have been removed since (the App uninstalled and
            // installed again gets a new id): look it up afresh, once.
            if (answer.Status == 404 && found && pass == 0)
            {
                installation = await FindInstallationAsync(request, key, refresh: true, ct).ConfigureAwait(false);
                continue;
            }

            throw TokenFailure(answer, request, installation);
        }
    }

    private async Task<long> FindInstallationAsync(GitHubAppTokenRequest request, RSA key, bool refresh, CancellationToken ct)
    {
        var repository = request.Repository!;
        var cacheKey = string.Join('|', request.ApiRoot.ToLowerInvariant(), request.App.AppId, repository.ToString().ToLowerInvariant());
        var now = _clock.GetUtcNow();
        if (!refresh && _installations.TryGetValue(cacheKey, out var cached) && now < cached.Until)
        {
            return cached.InstallationId ?? throw NotInstalled(request, repository, statusCode: 404);
        }

        var answer = await SendAsync(
            HttpMethod.Get,
            $"{request.ApiRoot}/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/installation",
            body: null, request.App, key, request.ApiRoot, ct).ConfigureAwait(false);

        if (answer.Status == 404)
        {
            _installations[cacheKey] = new CachedInstallation(null, _clock.GetUtcNow() + _options.NotInstalledLifetime);
            throw NotInstalled(request, repository, statusCode: 404);
        }

        if (answer.Status != 200)
        {
            throw Failure(answer, request.App, request.ApiRoot, $"find the installation of GitHub App {request.App.AppId} for {repository}");
        }

        long id;
        DateTimeOffset? suspended;
        string? account;
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out id) || id <= 0)
            {
                throw new GitHubAppException(
                    $"GitHub answered the installation lookup of GitHub App {request.App.AppId} for {repository} without an installation id ({request.ApiRoot}{RequestId(answer)}).");
            }

            suspended = Date(root, "suspended_at");
            account = root.TryGetProperty("account", out var accountElement) && accountElement.ValueKind == JsonValueKind.Object
                ? String(accountElement, "login")
                : null;
        }
        catch (JsonException ex)
        {
            throw new GitHubAppException(
                $"GitHub answered the installation lookup of GitHub App {request.App.AppId} for {repository} with a body that is not JSON ({request.ApiRoot}{RequestId(answer)}).", ex);
        }

        if (suspended is { } since)
        {
            throw new GitHubAppException(
                string.Create(CultureInfo.InvariantCulture,
                    $"installation {id} of GitHub App {request.App.AppId} on {account ?? repository.Owner} is suspended (since {since:u}): an owner of {account ?? repository.Owner} must unsuspend it under Settings > GitHub Apps."),
                statusCode: 200);
        }

        _installations[cacheKey] = new CachedInstallation(id, _clock.GetUtcNow() + _options.InstallationLifetime);
        return id;
    }

    /// <summary>
    /// Sends one request as the App, with the retries GitHub's answers call for, and returns the first answer that is
    /// not worth retrying. Every attempt signs a new JSON web token on the corrected clock.
    /// </summary>
    private async Task<Answer> SendAsync(
        HttpMethod method, string url, string? body, GitHubAppIdentity app, RSA key, string apiRoot, CancellationToken ct)
    {
        var resigned = false;
        for (var attempt = 1; ; attempt++)
        {
            var offset = _offsets.TryGetValue(apiRoot, out var known) ? known : TimeSpan.Zero;
            var jwt = GitHubAppJwt.Create(app.AppId, key, _clock.GetUtcNow() + offset);

            Answer? answer = null;
            Exception? failure = null;
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attemptCts.CancelAfter(_options.AttemptTimeout);
                try
                {
                    answer = await SendOnceAsync(method, url, body, jwt, apiRoot, attemptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failure = new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"no answer within {_options.AttemptTimeout.TotalSeconds:0} seconds"));
                }
                catch (HttpRequestException ex)
                {
                    failure = ex;
                }
            }

            if (answer is not null)
            {
                Learn(apiRoot, answer);

                // A refused token signed on a clock GitHub disagrees with is signed again on GitHub's, once.
                if (answer.Status == 401 && !resigned && answer.Offset is { } learned
                    && (learned - offset).Duration() > _options.SkewTolerance)
                {
                    resigned = true;
                    LogClockSkew(_logger, apiRoot, learned.TotalSeconds);
                    attempt--;
                    continue;
                }

                var wait = TransientWait(answer, attempt);
                if (wait is null)
                {
                    return answer;
                }

                if (wait.Value > _options.MaxRetryWait)
                {
                    throw new GitHubAppException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"GitHub ({apiRoot}) is rate limiting GitHub App {app.AppId} and asks for {Math.Ceiling(wait.Value.TotalSeconds):0} seconds' wait (HTTP {answer.Status}{RequestId(answer)}): {Message(answer)}. The next sync or run asks again."),
                        answer.Status);
                }

                if (attempt >= _options.MaxAttempts)
                {
                    throw new GitHubAppException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"GitHub ({apiRoot}) did not serve GitHub App {app.AppId} after {attempt} attempts; the last answer was HTTP {answer.Status}{RequestId(answer)}: {Message(answer)}."),
                        answer.Status);
                }

                LogRetry(_logger, apiRoot, attempt, $"HTTP {answer.Status}", wait.Value.TotalSeconds);
                await DelayAsync(wait.Value, ct).ConfigureAwait(false);
                continue;
            }

            if (attempt >= _options.MaxAttempts)
            {
                throw new GitHubAppException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"could not reach GitHub ({apiRoot}) for GitHub App {app.AppId} after {attempt} attempts: {SecretHygiene.RedactedMessage(failure!)}"),
                    failure!);
            }

            var backoff = Backoff(attempt);
            LogRetry(_logger, apiRoot, attempt, SecretHygiene.RedactedMessage(failure!), backoff.TotalSeconds);
            await DelayAsync(backoff, ct).ConfigureAwait(false);
        }
    }

    private async Task<Answer> SendOnceAsync(HttpMethod method, string url, string? body, string jwt, string apiRoot, CancellationToken ct)
    {
        var target = new Uri(url);
        var api = new Uri(apiRoot);
        for (var hop = 0; ; hop++)
        {
            using var message = new HttpRequestMessage(method, target);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd(UserAgent);
            message.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            if (body is not null)
            {
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new GitHubAppException(
                    $"GitHub ({apiRoot}) answered HTTP {status} without saying where to; the request was not repeated.");
                var next = location.IsAbsoluteUri ? location : new Uri(target, location);

                // The App's signed token goes to its own API alone: a redirect elsewhere is refused, not followed.
                if (!string.Equals(next.Scheme, api.Scheme, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(next.Authority, api.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitHubAppException(
                        $"GitHub ({apiRoot}) redirected a request to {next.GetLeftPart(UriPartial.Authority)}, which is not that API; the App's token is not sent there.");
                }

                // A POST is repeated only where HTTP keeps it a POST; GitHub's API does not redirect one otherwise.
                if (method == HttpMethod.Post && status is not (307 or 308))
                {
                    throw new GitHubAppException($"GitHub ({apiRoot}) answered a token request with HTTP {status}; the request was not repeated.");
                }

                if (hop >= MaxRedirects)
                {
                    throw new GitHubAppException($"GitHub ({apiRoot}) redirected a request more than {MaxRedirects} times; it was not followed further.");
                }

                target = next;
                continue;
            }

            var text = await ReadBodyAsync(response.Content, ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var offset = response.Headers.Date is { } date ? date - now : (TimeSpan?)null;

            // A Retry-After date is on GitHub's clock, like the Date header it is measured against.
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } until ? until - (now + (offset ?? TimeSpan.Zero)) : null)
                : null;
            return new Answer(
                status,
                text,
                offset,
                retryAfter,
                Header(response, "x-ratelimit-remaining"),
                Header(response, "x-ratelimit-reset"),
                Header(response, "x-github-request-id"),
                response.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null);
        }
    }

    private async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } length && length > _options.MaxBodyBytes)
        {
            throw new GitHubAppException(string.Create(CultureInfo.InvariantCulture,
                $"GitHub answered with a body of {length} bytes, larger than any answer these requests get; it was not read."));
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxBodyBytes)
            {
                throw new GitHubAppException(string.Create(CultureInfo.InvariantCulture,
                    $"GitHub answered with a body larger than {_options.MaxBodyBytes} bytes, more than any answer these requests get; it was not read."));
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>How long to wait before asking again, or null when the answer is final.</summary>
    private TimeSpan? TransientWait(Answer answer, int attempt)
    {
        var rateLimited = answer.Status == 429
            || (answer.Status == 403 && (answer.RetryAfter is not null
                || string.Equals(answer.RateLimitRemaining, "0", StringComparison.Ordinal)
                || Message(answer).Contains("rate limit", StringComparison.OrdinalIgnoreCase)));
        if (rateLimited)
        {
            if (answer.RetryAfter is { } retryAfter)
            {
                return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
            }

            if (string.Equals(answer.RateLimitRemaining, "0", StringComparison.Ordinal)
                && long.TryParse(answer.RateLimitReset, NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            {
                var githubNow = _clock.GetUtcNow() + (answer.Offset ?? TimeSpan.Zero);
                var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - githubNow;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }

            // GitHub asks for at least a minute after a secondary rate limit that names no wait.
            return answer.Status == 403 ? TimeSpan.FromMinutes(1) : Backoff(attempt);
        }

        return answer.Status is >= 500 and <= 599 ? Backoff(attempt) : null;
    }

    private TimeSpan Backoff(int attempt) => _options.FirstBackoff * Math.Pow(2, attempt - 1);

    private Task DelayAsync(TimeSpan wait, CancellationToken ct)
        => _options.Delay is { } delay ? delay(wait, ct) : Task.Delay(wait, _clock, ct);

    private void Learn(string apiRoot, Answer answer)
    {
        if (answer.Offset is { } offset)
        {
            _offsets[apiRoot] = offset;
        }
    }

    private GitHubAppToken ReadToken(Answer answer, GitHubAppTokenRequest request, long installation)
    {
        // The body holds the token, so no message quotes it.
        string? token;
        DateTimeOffset expiresAt;
        var permissions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? selection;
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GitHubAppException(
                    $"GitHub answered the token request of {request.Describe()} without a usable token and expiry (HTTP {answer.Status}{RequestId(answer)}).");
            }

            token = String(root, "token");
            var expires = Date(root, "expires_at");
            if (string.IsNullOrWhiteSpace(token) || expires is null || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new GitHubAppException(
                    $"GitHub answered the token request of {request.Describe()} without a usable token and expiry (HTTP {answer.Status}{RequestId(answer)}).");
            }

            expiresAt = expires.Value;
            if (root.TryGetProperty("permissions", out var granted) && granted.ValueKind == JsonValueKind.Object)
            {
                foreach (var permission in granted.EnumerateObject())
                {
                    if (permission.Value.ValueKind == JsonValueKind.String)
                    {
                        permissions[permission.Name] = permission.Value.GetString()!;
                    }
                }
            }

            selection = String(root, "repository_selection");
        }
        catch (JsonException)
        {
            throw new GitHubAppException(
                $"GitHub answered the token request of {request.Describe()} with a body that is not the token response (HTTP {answer.Status}{RequestId(answer)}).");
        }

        // GitHub's expiry is on GitHub's clock; this host times the token on its own.
        var local = expiresAt - (answer.Offset ?? TimeSpan.Zero);
        if (local <= _clock.GetUtcNow())
        {
            throw new GitHubAppException(string.Create(CultureInfo.InvariantCulture,
                $"GitHub handed {request.Describe()} a token that has already expired ({expiresAt:u}){RequestId(answer)}; check this host's clock."));
        }

        return new GitHubAppToken(token, local, installation, request.Repository, permissions, selection);
    }

    private static string? TokenRequestBody(GitHubAppTokenRequest request)
    {
        if (request.Repository is null && request.Permissions.Count == 0)
        {
            return "{}";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (request.Repository is { } repository)
            {
                writer.WriteStartArray("repositories");
                writer.WriteStringValue(repository.Name);
                writer.WriteEndArray();
            }

            if (request.Permissions.Count > 0)
            {
                writer.WriteStartObject("permissions");
                foreach (var (name, level) in request.Permissions)
                {
                    writer.WriteString(name, level);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>What a refused token request means, in words that say what to change.</summary>
    private static GitHubAppException TokenFailure(Answer answer, GitHubAppTokenRequest request, long installation)
    {
        var app = request.App.AppId;
        var message = Message(answer);
        var where = string.Create(CultureInfo.InvariantCulture, $"installation {installation} of GitHub App {app}");
        switch (answer.Status)
        {
            case 404:
                return new GitHubAppException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{where} was not found (HTTP 404{RequestId(answer)}): the App was uninstalled, or {installation} is another App's installation. The id ends the installation's settings page URL (the owner's Settings > GitHub Apps > Configure)."),
                    404);
            case 422 when request.Repository is { } repository && message.Contains("repositor", StringComparison.OrdinalIgnoreCase):
                return new GitHubAppNotInstalledException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{where} does not reach {repository} (HTTP 422{RequestId(answer)}): {message}. Add the repository to the installation's repository access, or install the App on {repository.Owner}."),
                    repository.ToString(), 422);
            case 422 when message.Contains("permission", StringComparison.OrdinalIgnoreCase):
                return new GitHubAppException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{where} is not granted {GitHubAppPermissions.Format(request.Permissions)} (HTTP 422{RequestId(answer)}): {message}. Grant the App these repository permissions (its settings, Permissions & events) and have the owner accept the change."),
                    422);
            case 403 when message.Contains("suspend", StringComparison.OrdinalIgnoreCase):
                return new GitHubAppException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{where} is suspended (HTTP 403{RequestId(answer)}): {message}. An owner of the account must unsuspend it under Settings > GitHub Apps."),
                    403);
            default:
                return Failure(answer, request.App, request.ApiRoot, $"mint a token for {request.Describe()} on installation {installation}");
        }
    }

    private static GitHubAppException Failure(Answer answer, GitHubAppIdentity app, string apiRoot, string what)
    {
        var message = Message(answer);
        return answer.Status switch
        {
            401 => new GitHubAppException(
                string.Create(CultureInfo.InvariantCulture,
                    $"GitHub ({apiRoot}) refused the signed token of GitHub App {app.AppId} (HTTP 401{RequestId(answer)}): {message}. Check that {app.AppId} is the App's client ID or App ID, that the private key ({app.Key.Description}) is one of the App's current keys, and that this host's clock is right."),
                401),
            _ => new GitHubAppException(
                string.Create(CultureInfo.InvariantCulture,
                    $"GitHub ({apiRoot}) answered HTTP {answer.Status}{RequestId(answer)} when asked to {what}: {message}."),
                answer.Status),
        };
    }

    private static GitHubAppNotInstalledException NotInstalled(GitHubAppTokenRequest request, GitHubRepositoryName repository, int statusCode)
        => new(
            $"GitHub App {request.App.AppId} is not installed on {repository} ({request.ApiRoot}): GitHub knows no installation of the App that reaches it. "
            + $"Install the App on {repository.Owner} with access to {repository.Name}, or check the repository's name.",
            repository.ToString(),
            statusCode);

    /// <summary>GitHub's own message from an error body, redacted and shortened; never a success body.</summary>
    private static string Message(Answer answer)
    {
        if (answer.Status is >= 200 and < 300 || string.IsNullOrWhiteSpace(answer.Body))
        {
            return "(no message)";
        }

        string text;
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            text = document.RootElement.ValueKind == JsonValueKind.Object && String(document.RootElement, "message") is { } message
                ? message
                : answer.Body;
        }
        catch (JsonException)
        {
            text = answer.Body;
        }

        // GitHub never echoes a credential, but a message quoting one must not carry it on: GitHub's tokens are scrubbed
        // by SecretHygiene, and a JSON web token (the App's signed token) here.
        text = JsonWebToken().Replace(SecretHygiene.RedactedMessage(text.ReplaceLineEndings(" ").Trim()), "[redacted]");
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] + "..." : text;
    }

    private static string RequestId(Answer answer)
        => answer.RequestId is { Length: > 0 } id ? $", GitHub request {id}" : string.Empty;

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Date(JsonElement element, string name)
        => String(element, name) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    [System.Text.RegularExpressions.GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex JsonWebToken();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Minted an installation token of GitHub App {AppId} on installation {InstallationId} for {Repository}, expiring {ExpiresUtc:o}")]
    private static partial void LogMinted(ILogger logger, string appId, long installationId, string repository, DateTimeOffset expiresUtc);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "GitHub ({ApiRoot}) did not serve attempt {Attempt} ({Cause}); asking again in {WaitSeconds:0.#} seconds")]
    private static partial void LogRetry(ILogger logger, string apiRoot, int attempt, string cause, double waitSeconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "This host's clock is {SkewSeconds:0} seconds off GitHub's ({ApiRoot}); the App's token was signed again on GitHub's time. Set the host's clock right")]
    private static partial void LogClockSkew(ILogger logger, string apiRoot, double skewSeconds);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "GitHub handed installation {InstallationId} a token expiring {ExpiresUtc:o}, too soon to hand out again; it serves one operation")]
    private static partial void LogShortLivedToken(ILogger logger, long installationId, DateTimeOffset expiresUtc);

    private sealed record CachedToken(GitHubAppToken Token, DateTimeOffset ReuseUntil);

    private sealed record CachedInstallation(long? InstallationId, DateTimeOffset Until);

    /// <summary>One answer from GitHub. <see cref="Offset"/> is GitHub's clock minus this host's, from its Date header.
    /// The body of a token answer holds the token, so <see cref="ToString"/> leaves it out.</summary>
    private sealed record Answer(
        int Status, string Body, TimeSpan? Offset, TimeSpan? RetryAfter, string? RateLimitRemaining, string? RateLimitReset,
        string? RequestId, string? Link)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"HTTP {Status}{RequestId}");
    }
}

/// <summary>
/// The JSON web token a GitHub App authenticates with: RS256-signed by the App's private key, issued 60 seconds in the
/// past (GitHub's guard against clock drift) and expiring nine minutes ahead (GitHub accepts at most ten), with the App's
/// client ID or App ID as the issuer.
/// </summary>
internal static class GitHubAppJwt
{
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");

    internal static string Create(string appId, RSA key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(key);

        var seconds = now.ToUnixTimeSeconds();
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteNumber("iat", seconds - 60);
            writer.WriteNumber("exp", seconds + 540);

            // A numeric App ID is written as a number, the way GitHub's examples write it; a client ID as a string.
            if (long.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
            {
                writer.WriteNumber("iss", numeric);
            }
            else
            {
                writer.WriteString("iss", appId);
            }

            writer.WriteEndObject();
        }

        var signingInput = Base64Url.EncodeToString(Header) + "." + Base64Url.EncodeToString(payload.ToArray());
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }
}
