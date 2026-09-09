using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime;

/// <summary>The raw outcome of one HTTP fetch: status, body bytes, and the response/content headers.</summary>
public sealed record HttpFetchResult(
    HttpStatusCode Status,
    byte[] Body,
    HttpResponseHeaders Headers,
    HttpContentHeaders? ContentHeaders)
{
    /// <summary>The response Content-Type media type, or null.</summary>
    public string? ContentType => ContentHeaders?.ContentType?.MediaType;

    /// <summary>The RFC 5988 <c>Link: &lt;url&gt;; rel="next"</c> URL from the response, or null.</summary>
    public string? NextLink => Headers.TryGetValues("Link", out var values) ? ParseNextLink(string.Join(", ", values)) : null;

    private static string? ParseNextLink(string linkHeader)
    {
        foreach (var part in linkHeader.Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length < 2)
            {
                continue;
            }

            var url = segments[0].Trim().Trim('<', '>', ' ');
            for (var i = 1; i < segments.Length; i++)
            {
                var rel = segments[i].Trim();
                if (rel.StartsWith("rel", StringComparison.OrdinalIgnoreCase)
                    && rel.Contains("next", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Executes a single HTTP request through the shared reliability stack: rate limit, SSRF guard, bounded retry with
/// backoff, and a hard response-size cap. Retries rebuild the request from the factory (a message cannot be resent),
/// so every attempt is a clean request. A 2xx returns the bytes; a non-retryable or exhausted failure throws with
/// the status and a truncated body preview.
/// </summary>
public sealed class HttpExecutor
{
    static HttpExecutor()
    {
        // Some APIs serve text/JSON in a legacy single-byte code page (e.g. Fjord1's Shiplog feed returns
        // application/json; charset=ISO-8859-1). Register the code-page provider so those charsets resolve
        // and the body can be normalized to UTF-8 before landing (see NormalizeToUtf8).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly HttpClient _client;
    private readonly RetryPolicy _retry;
    private readonly RateLimiter _rateLimiter;
    private readonly UrlGuard _urlGuard;
    private readonly long _maxResponseBytes;
    private readonly TimeProvider _time;

    public HttpExecutor(HttpClient client, RetryPolicy retry, RateLimiter rateLimiter, UrlGuard urlGuard, long maxResponseBytes, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(urlGuard);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _retry = retry;
        _rateLimiter = rateLimiter;
        _urlGuard = urlGuard;
        _maxResponseBytes = maxResponseBytes;
        _time = time;
    }

    /// <param name="requestFactory">Builds a fresh request per attempt.</param>
    /// <param name="allowStatuses">Non-2xx statuses to return instead of throwing (e.g. a 202 pagination sentinel).</param>
    /// <param name="responseCharset">The response body's ACTUAL charset, overriding the declared one, for
    /// endpoints that label their payload wrongly (see <see cref="AcquireRequest.ResponseCharset"/>).</param>
    /// <param name="ct">Cancels the send, including the rate-limit wait between attempts.</param>
    public async Task<HttpFetchResult> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        IReadOnlySet<int>? allowStatuses = null,
        string? responseCharset = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        for (var attempt = 1; ; attempt++)
        {
            await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

            using var request = requestFactory();
            if (request.RequestUri is { } uri)
            {
                _urlGuard.Check(uri);
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = response.StatusCode;
                var code = (int)status;

                if (response.IsSuccessStatusCode || (allowStatuses?.Contains(code) ?? false))
                {
                    var body = await ReadCappedAsync(response, ct).ConfigureAwait(false);
                    body = NormalizeToUtf8(body, response.Content.Headers, responseCharset);
                    return new HttpFetchResult(status, body, response.Headers, response.Content.Headers);
                }

                var decision = _retry.Next(attempt, status, response.Headers);
                if (!decision.ShouldRetry)
                {
                    var preview = await PreviewAsync(response, ct).ConfigureAwait(false);
                    throw new HttpStatusException(code, $"HTTP {code} {status} from {request.RequestUri}: {preview}");
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new SqlFlowException($"HTTP transport failure calling {request.RequestUri}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                // A connection reset or premature close WHILE STREAMING THE RESPONSE BODY. Because the send uses
                // HttpCompletionOption.ResponseHeadersRead, the body is read (in ReadCappedAsync) after SendAsync has
                // already returned, so a mid-body failure surfaces here as a raw IOException ("An existing connection
                // was forcibly closed by the remote host", "The response ended prematurely") rather than as an
                // HttpRequestException. Without this arm it would escape the retry loop and fail the whole run on a
                // single dropped connection; treat it as the transient transport failure it is and retry the request
                // from the factory. The response-size cap throws SqlFlowException (not IOException), so an oversized
                // body is still permanent and never retried here.
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new SqlFlowException($"HTTP transport failure reading the response from {request.RequestUri}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A per-request timeout (not caller cancellation): treat as a transient transport failure.
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new SqlFlowException($"HTTP request to {request.RequestUri} timed out after {_client.Timeout.TotalSeconds:0}s.", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxResponseBytes)
            {
                throw new SqlFlowException(
                    $"Response from {response.RequestMessage?.RequestUri} exceeds the {_maxResponseBytes / (1024 * 1024)} MB limit. " +
                    "Raise reliability.maxResponseBytes or narrow the request window.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Re-encodes a text/JSON/XML response body to UTF-8 when it declares a non-UTF-8 charset, so everything
    /// downstream (the raw landing files and the UTF-8-only JSON/XML readers) sees valid UTF-8. The whole V3
    /// pipeline is UTF-8; landing a legacy single-byte code page verbatim would either be rejected by the strict
    /// UTF-8 JSON reader or silently mangle non-ASCII characters. Bodies with no declared charset, an explicit
    /// UTF-8/ASCII charset, a non-text media type, or an unknown charset are returned unchanged (byte-for-byte),
    /// so this is a no-op for the common case and never corrupts a payload it cannot confidently decode.
    /// </summary>
    internal static byte[] NormalizeToUtf8(byte[] body, HttpContentHeaders? contentHeaders, string? charsetOverride = null)
    {
        if (body.Length == 0)
        {
            return body;
        }

        // An authored override is authoritative: the endpoint declares its charset wrongly (or not at all),
        // so the declared header and the text-likeness gate are deliberately bypassed. An override that does
        // not resolve is an authoring error and throws, unlike a bad declared charset which is tolerated.
        if (!string.IsNullOrWhiteSpace(charsetOverride))
        {
            Encoding forced;
            try
            {
                forced = Encoding.GetEncoding(charsetOverride.Trim());
            }
            catch (ArgumentException ex)
            {
                throw new SqlFlowException($"responseCharset '{charsetOverride}' is not a recognized encoding.", ex);
            }

            return forced.CodePage == Encoding.UTF8.CodePage ? body : Encoding.UTF8.GetBytes(forced.GetString(body));
        }

        var charset = contentHeaders?.ContentType?.CharSet?.Trim().Trim('"');
        if (string.IsNullOrEmpty(charset)
            || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf8", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("ascii", StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        if (!IsTextLike(contentHeaders?.ContentType?.MediaType))
        {
            return body;
        }

        Encoding source;
        try
        {
            source = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            // An unrecognized charset: leave the bytes untouched rather than risk a wrong decode.
            return body;
        }

        if (source.CodePage == Encoding.UTF8.CodePage)
        {
            return body;
        }

        return Encoding.UTF8.GetBytes(source.GetString(body));
    }

    /// <summary>True for payloads that are character data (so a charset transcode is meaningful and safe): a
    /// missing media type, any <c>text/*</c>, or a structured type whose subtype is or ends in json/xml.</summary>
    private static bool IsTextLike(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> PreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length <= 512 ? text : text[..512] + "…";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            return "(response body unavailable)";
        }
    }
}

/// <summary>
/// The concrete auth material to merge onto a request: header additions (already including any prefix and encoding)
/// and query additions. Precomputed once (or per iteration) by the auth resolver so a transport only merges. It
/// never carries the raw secret separately: the values are the final header/query values.
/// </summary>
public sealed record AppliedAuth(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query)
{
    public static readonly AppliedAuth None = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static AppliedAuth Header(string name, string value) => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value },
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static AppliedAuth QueryParam(string name, string value) => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });

    public static string BasicHeader(string username, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
}
