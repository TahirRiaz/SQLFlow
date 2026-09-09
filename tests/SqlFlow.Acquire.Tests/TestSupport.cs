using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SqlFlow.Acquire.Engine;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Acquire.Tests;

/// <summary>A scripted HTTP handler: routes each request to a registered responder by "METHOD path", records every
/// request (url + body), and fails loudly on an unmatched route so a test never silently passes on the wrong call.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, string, HttpResponseMessage?>> _routes = [];
    private readonly List<CapturedRequest> _requests = [];
    // The engine now issues an item's fan-out concurrently, so several SendAsync calls capture at once; guard the
    // list (and hand out a snapshot) so the harness matches the product's own thread-safe landing sink.
    private readonly Lock _requestsLock = new();

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requestsLock)
            {
                return _requests.ToList();
            }
        }
    }

    public StubHttpHandler Route(Func<HttpRequestMessage, string, HttpResponseMessage?> responder)
    {
        _routes.Add(responder);
        return this;
    }

    /// <summary>Registers a JSON responder for a request whose URI contains <paramref name="uriContains"/>.</summary>
    public StubHttpHandler Json(string uriContains, Func<HttpRequestMessage, string> json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
        => Route((request, _) =>
        {
            if (request.RequestUri!.ToString().Contains(uriContains, StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(json(request), Encoding.UTF8, "application/json"),
                };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            }

            return null;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var captured = new CapturedRequest(request.Method.Method, request.RequestUri!, body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        lock (_requestsLock)
        {
            _requests.Add(captured);
        }

        foreach (var route in _routes)
        {
            if (route(request, body) is { } response)
            {
                return response;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no stub route matched {request.Method} {request.RequestUri}"),
        };
    }
}

internal sealed record CapturedRequest(string Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>A secret resolver for tests: expands <c>${test:name}</c> from a map and leaves everything else untouched
/// (so a literal token used directly as a secretRef resolves to itself).</summary>
internal sealed class FakeSecrets : ISecretResolver
{
    private readonly ConcurrentDictionary<string, string> _values;

    public FakeSecrets(params (string Name, string Value)[] values)
        => _values = new ConcurrentDictionary<string, string>(values.ToDictionary(v => v.Name, v => v.Value));

    public string Resolve(string value) => Expand(value);

    public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(Expand(value));

    private string Expand(string value)
    {
        foreach (var (name, secret) in _values)
        {
            value = value.Replace($"${{test:{name}}}", secret, StringComparison.Ordinal);
        }

        return value;
    }
}

/// <summary>Builds an <see cref="AcquireEngine"/> wired to a stub HTTP handler and a temp-directory landing store.</summary>
internal static class TestEngine
{
    public static AcquireEngine Create(
        StubHttpHandler handler,
        ISecretResolver secrets,
        TimeProvider time,
        out string landingDir,
        IAcquireWatermarkProbe? watermarkProbe = null)
    {
        landingDir = Path.Combine(Path.GetTempPath(), "sqlflow-acquire-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(landingDir);
        var store = new CompositeRawLandingStore([new LocalRawLandingStore()]);
        var transports = new IAcquireTransport[] { new HttpTransport() };
        return new AcquireEngine(
            store, new AuthResolver(secrets), secrets, transports, time,
            _ => new HttpClient(handler, disposeHandler: false), watermarkProbe);
    }

    public static IReadOnlyList<string> LandedFiles(string landingDir)
        => Directory.Exists(landingDir)
            ? Directory.EnumerateFiles(landingDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];
}
