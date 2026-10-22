using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.SourceControl.GitHub;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>One RSA key per test run: generating a 2048-bit key takes long enough to matter across a suite.</summary>
internal static class TestKeys
{
    private static readonly Lazy<RSA> Shared = new(() => RSA.Create(2048));

    /// <summary>The key, for verifying what the client signed.</summary>
    public static RSA Key => Shared.Value;

    /// <summary>The key as GitHub hands it out: PKCS#1 PEM.</summary>
    public static string Pkcs1Pem => Key.ExportRSAPrivateKeyPem();

    /// <summary>The key converted to PKCS#8 PEM.</summary>
    public static string Pkcs8Pem => Key.ExportPkcs8PrivateKeyPem();
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A secret provider for the scheme <c>testkey</c>: a test names a value by locator, so a key reference resolves
/// without the process environment (shared across parallel tests) or a vault.</summary>
internal sealed class TestKeyProvider(IReadOnlyDictionary<string, string> values) : ISecretProvider
{
    public string Scheme => "testkey";

    public string Resolve(string locator)
        => values.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"testkey '{locator}' is not set.");
}

/// <summary>One request the fake GitHub received, read before the client disposed it.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary>The JSON web token the request carried, decoded.</summary>
    public DecodedJwt Jwt => DecodedJwt.Parse(Authorization!["Bearer ".Length..]);
}

/// <summary>A JSON web token taken apart: its header, its claims, and whether the test key signed it.</summary>
internal sealed record DecodedJwt(JsonElement Header, JsonElement Claims, bool SignedByTestKey)
{
    public long IssuedAt => Claims.GetProperty("iat").GetInt64();

    public long ExpiresAt => Claims.GetProperty("exp").GetInt64();

    public static DecodedJwt Parse(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("not a JSON web token");
        }

        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement.Clone();
        var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement.Clone();
        var signed = TestKeys.Key.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new DecodedJwt(header, claims, signed);
    }
}

/// <summary>
/// A scripted GitHub API: each request is recorded and answered by <see cref="Answer"/>, which a test sets. Answers
/// are built with the helpers below so they carry what GitHub sends (a Date header, a request id).
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private int _count;

    public FakeGitHub(ManualClock clock) => Clock = clock;

    public ManualClock Clock { get; }

    /// <summary>How the next request is answered; the second argument is the request's 1-based number.</summary>
    public Func<RecordedRequest, int, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; }
        = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public IReadOnlyList<RecordedRequest> Posts => Requests.Where(r => r.Method == HttpMethod.Post).ToList();

    public IReadOnlyList<RecordedRequest> Gets => Requests.Where(r => r.Method == HttpMethod.Get).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), headers, body);
        Requests.Enqueue(recorded);
        return await Answer(recorded, Interlocked.Increment(ref _count), cancellationToken);
    }

    /// <summary>A JSON answer dated <paramref name="date"/> (the fake's clock when null).</summary>
    public HttpResponseMessage Json(HttpStatusCode status, string json, DateTimeOffset? date = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Date = date ?? Clock.Now;
        response.Headers.Add("x-github-request-id", "ABCD:1234");
        return response;
    }

    /// <summary>A token answer: the token, expiring an hour after <paramref name="date"/> unless told otherwise.</summary>
    public HttpResponseMessage Token(string token, DateTimeOffset? date = null, DateTimeOffset? expiresAt = null, string? permissionsJson = null)
    {
        var at = date ?? Clock.Now;
        var expires = (expiresAt ?? at.AddHours(1)).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return Json(
            HttpStatusCode.Created,
            $$"""{"token":"{{token}}","expires_at":"{{expires}}","permissions":{{permissionsJson ?? "{\"contents\":\"read\",\"metadata\":\"read\"}"}},"repository_selection":"selected"}""",
            at);
    }

    /// <summary>An installation answer for the repository lookup.</summary>
    public HttpResponseMessage Installation(long id, string account = "acme", string? suspendedAt = null)
        => Json(
            HttpStatusCode.OK,
            $$"""{"id":{{id}},"account":{"login":"{{account}}","type":"Organization"},"suspended_at":{{(suspendedAt is null ? "null" : $"\"{suspendedAt}\"")}}}""");

    /// <summary>An error answer with GitHub's message.</summary>
    public HttpResponseMessage Error(HttpStatusCode status, string message, DateTimeOffset? date = null)
        => Json(status, JsonSerializer.Serialize(new { message, documentation_url = "https://docs.github.com/rest" }), date);
}

/// <summary>Builds the pieces a test needs around one fake GitHub.</summary>
internal sealed class GitHubAppHarness : IDisposable
{
    public const string AppId = "Iv23liTestApp01";

    public GitHubAppHarness(Dictionary<string, string>? environment = null, GitHubAppClientOptions? options = null)
    {
        Clock = new ManualClock(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        GitHub = new FakeGitHub(Clock);
        Environment = environment ?? new Dictionary<string, string>
        {
            [GitHubAppEnvironment.AppIdVariable] = AppId,
            [GitHubAppEnvironment.PrivateKeyVariable] = TestKeys.Pkcs1Pem,
        };
        Keys = new Dictionary<string, string> { ["app-key"] = TestKeys.Pkcs1Pem };

        // The client reads the resolver through a factory, as the engine registers it, so the resolver can hold the
        // provider that uses the client.
        Client = new GitHubAppTokenClient(
            () => Resolver!, Clock, logger: null, GitHub,
            options ?? new GitHubAppClientOptions { Delay = RecordDelay, AttemptTimeout = TimeSpan.FromSeconds(5) });
        Resolver = new SecretResolver([new TestKeyProvider(Keys), new EnvSecretProvider(), Provider]);
    }

    public ManualClock Clock { get; }

    public FakeGitHub GitHub { get; }

    public Dictionary<string, string> Environment { get; }

    public Dictionary<string, string> Keys { get; }

    public GitHubAppTokenClient Client { get; }

    public SecretResolver Resolver { get; }

    /// <summary>The waits the client asked for between attempts, in order. No test actually waits.</summary>
    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    /// <summary>The provider over this harness's client and environment.</summary>
    public GitHubAppSecretProvider Provider => _provider ??= new GitHubAppSecretProvider(Client, Variable);

    private GitHubAppSecretProvider? _provider;

    /// <summary>The environment as a variable lookup.</summary>
    public string? Variable(string name) => Environment.TryGetValue(name, out var value) ? value : null;

    /// <summary>The host settings this harness's environment describes.</summary>
    public GitHubAppSettings Settings => GitHubAppEnvironment.Read(Variable);

    /// <summary>A token request parsed from <paramref name="locator"/> against this harness's host settings.</summary>
    public GitHubAppTokenRequest Request(string locator) => GitHubAppReference.Parse(locator, Settings);

    public Task RecordDelay(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays.Enqueue(wait);
        return Task.CompletedTask;
    }

    public void Dispose() => Client.Dispose();
}
