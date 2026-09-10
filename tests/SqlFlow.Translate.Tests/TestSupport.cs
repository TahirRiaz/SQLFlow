using System.Collections.Concurrent;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Translate.Tests;

/// <summary>An in-memory <see cref="IExportDestination"/>: writes buffer into a dictionary keyed by location,
/// and reads hand back the written bytes, so the writer and the delivery step exercise the real seam.</summary>
internal sealed class MemoryDestination : IExportDestination
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string location) => true;

    public Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
        => Task.FromResult<Stream>(new CapturingStream(this, location));

    public Task DeleteIfExistsAsync(string location, CancellationToken ct = default)
    {
        Files.TryRemove(location, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeAsync(string location, CancellationToken ct = default)
        => Task.FromResult(Files.TryGetValue(location, out var bytes) ? bytes.LongLength : 0);

    public Task<Stream> OpenReadAsync(string location, CancellationToken ct = default)
        => Files.TryGetValue(location, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new SqlFlowException($"Cannot read '{location}': the file does not exist.");

    public string Text(string location) => Encoding.UTF8.GetString(Files[location]);

    private sealed class CapturingStream(MemoryDestination owner, string location) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Files[location] = ToArray();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A secret resolver that expands <c>${env:NAME}</c>-style references from a fixed map and leaves
/// everything else verbatim, mirroring the production resolver's reference syntax.</summary>
internal sealed class MapSecretResolver(IReadOnlyDictionary<string, string> secrets) : ISecretResolver
{
    public string Resolve(string value)
    {
        var result = value;
        foreach (var (reference, resolved) in secrets)
        {
            result = result.Replace(reference, resolved, StringComparison.Ordinal);
        }

        return result;
    }

    public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(Resolve(value));
}

/// <summary>Records every outbound request (method, URL, headers, body) and answers with a scripted status.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Sent(string Method, string Url, string Body, IReadOnlyDictionary<string, string> Headers, string? ContentType);

    private readonly Func<Sent, System.Net.HttpStatusCode> _respond;

    public RecordingHandler(Func<Sent, System.Net.HttpStatusCode>? respond = null)
    {
        _respond = respond ?? (_ => System.Net.HttpStatusCode.OK);
    }

    public List<Sent> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(
            request.Method.Method,
            // AbsoluteUri preserves percent-escaping (ToString would decode %20 back to a space).
            request.RequestUri!.AbsoluteUri,
            body,
            headers,
            request.Content?.Headers.ContentType?.MediaType);
        lock (Requests)
        {
            Requests.Add(sent);
        }

        return new HttpResponseMessage(_respond(sent)) { Content = new StringContent("{}") };
    }
}
