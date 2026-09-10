using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Export;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Translate;

namespace SqlFlow.Translate;

/// <summary>
/// The delivery step: reads the run's saved files back from the destination and posts their content to the
/// declared API through the shared acquisition HTTP stack (auth resolution, retry with backoff, rate limiting,
/// SSRF guarding, response-size cap). What goes on the wire is exactly what was saved: per-document files post
/// one request per file (or grouped into array batches), a JSON Lines file posts its lines in batches, and an
/// array file posts as one request. A status listed in <c>reliability.skipStatusCodes</c> marks that one request
/// a tolerated skip; anything else non-retryable fails the run, with every already-delivered request counted.
/// </summary>
internal sealed class TranslateInvoker
{
    private sealed record Batch(string Body, long Documents, IReadOnlyDictionary<string, object?>? Row, string Label);

    private readonly ISecretResolver _secrets;
    private readonly Func<AcquireReliability, HttpClient> _httpClientFactory;
    private readonly TimeProvider _time;

    public TranslateInvoker(ISecretResolver secrets, Func<AcquireReliability, HttpClient> httpClientFactory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(time);
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;
        _time = time;
    }

    public async Task<(long Sent, long Skipped)> DeliverAsync(
        TranslateFlow flow,
        IReadOnlyList<TranslateSavedFile> savedFiles,
        IExportDestination destination,
        IRunEventSink events,
        CancellationToken ct)
    {
        var invoke = flow.Invoke!;
        var reliability = invoke.Reliability;

        var totalDocuments = savedFiles.Sum(f => f.Documents);
        if (savedFiles.Count == 0 || (totalDocuments == 0 && flow.Output.Mode != TranslateOutputMode.Array))
        {
            // Nothing saved means nothing to deliver. The array layout still posts its (empty) array: the saved
            // file is the current truth and the endpoint receives exactly that.
            events.Log(RunLogLevel.Info, "invoke.skip", "no documents were produced; no API request is sent.");
            return (0, 0);
        }

        using var client = _httpClientFactory(reliability);
        var retry = new RetryPolicy(reliability.Retry, _time);
        // One shared rate limiter, so the token request and the data requests together respect the cap.
        var limiter = new RateLimiter(reliability.RateLimitRps, _time);
        var dataHttp = new HttpExecutor(client, retry, limiter, new UrlGuard(reliability.UrlAllowlist), reliability.MaxResponseBytes, _time);
        // The auth/token endpoint keeps the IP-level SSRF guard but not the data-host allowlist, exactly as the
        // acquisition engine composes it (the token issuer is routinely a different host).
        var authHttp = new HttpExecutor(client, retry, limiter, new UrlGuard([]), reliability.MaxResponseBytes, _time);

        var auth = await new AuthResolver(_secrets).ResolveAsync(
            invoke.Auth, authHttp, new TemplateContext(_time.GetUtcNow()), ct).ConfigureAwait(false);

        // Headers are static per run; values may carry ${...} secret references. Content-Type is carried on the
        // content itself (HttpClient rejects it as a request header), defaulting to application/json.
        var headers = new List<(string Name, string Value)>();
        var contentType = "application/json";
        foreach (var (name, value) in invoke.Headers)
        {
            var resolved = await _secrets.ResolveAsync(value, ct).ConfigureAwait(false);
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = resolved;
            }
            else
            {
                headers.Add((name, resolved));
            }
        }

        try
        {
            // Validate once, up front; each request parses its own instance (header values are mutable and
            // must not be shared across requests).
            _ = MediaTypeHeaderValue.Parse(contentType);
        }
        catch (FormatException ex)
        {
            throw new SqlFlowException($"invoke.headers Content-Type '{contentType}' is not a valid media type.", ex);
        }

        var urlHasTokens = TranslateTemplateText.HasTokens(invoke.Url);
        // A token-free URL resolves its secrets once; a tokenized one re-renders (and re-resolves) per document.
        var staticUrl = urlHasTokens ? null : await _secrets.ResolveAsync(invoke.Url, ct).ConfigureAwait(false);

        var skipStatuses = reliability.SkipStatusCodes.ToHashSet();
        long sent = 0;
        long skipped = 0;

        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;
        using var gate = new SemaphoreSlim(reliability.Concurrency, reliability.Concurrency);
        var tasks = new List<Task>();

        async Task SendOneAsync(Batch batch)
        {
            try
            {
                var renderedUrl = urlHasTokens ? RenderUrl(invoke.Url, batch.Row) : invoke.Url;
                var finalUrl = AppendQuery(
                    staticUrl ?? await _secrets.ResolveAsync(renderedUrl, failureCts.Token).ConfigureAwait(false),
                    auth.Query);

                try
                {
                    await dataHttp.SendAsync(
                        () => BuildRequest(invoke.Method, finalUrl, headers, auth.Headers, batch.Body, contentType),
                        ct: failureCts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref sent);
                    // The rendered (pre-secret, pre-auth) URL is safe to log; the final one may carry key material.
                    events.Log(RunLogLevel.Debug, "invoke.request",
                        $"{invoke.Method} {renderedUrl}: delivered {batch.Documents} document(s) ({batch.Label})");
                }
                catch (HttpStatusException ex) when (skipStatuses.Contains(ex.StatusCode))
                {
                    Interlocked.Increment(ref skipped);
                    events.Log(RunLogLevel.Info, "invoke.skip",
                        $"{batch.Label}: tolerated HTTP {ex.StatusCode}: {SecretHygiene.RedactedMessage(ex)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // First failure wins; cancel the producer and the other in-flight requests.
                Interlocked.CompareExchange(ref failure, ex, null);
                await failureCts.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await foreach (var batch in EnumerateBatchesAsync(invoke, flow.Output.Mode, savedFiles, destination, failureCts.Token)
                               .ConfigureAwait(false))
            {
                await gate.WaitAsync(failureCts.Token).ConfigureAwait(false);
                tasks.Add(SendOneAsync(batch));
            }
        }
        catch (OperationCanceledException) when (failure is not null)
        {
            // The producer stopped because a send failed; the real error surfaces below.
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (failure is not null)
        {
            throw new SqlFlowException(
                $"API delivery failed after {Interlocked.Read(ref sent)} request(s) were delivered: {failure.Message}", failure);
        }

        events.Log(RunLogLevel.Info, "invoke.end",
            $"delivered {sent} request(s) to {invoke.Url}" + (skipped > 0 ? $", tolerated {skipped} skip(s)" : string.Empty));
        return (sent, skipped);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Batching over the saved files
    // ---------------------------------------------------------------------------------------------------------

    private static async IAsyncEnumerable<Batch> EnumerateBatchesAsync(
        TranslateInvoke invoke,
        TranslateOutputMode mode,
        IReadOnlyList<TranslateSavedFile> savedFiles,
        IExportDestination destination,
        [EnumeratorCancellation] CancellationToken ct)
    {
        switch (mode)
        {
            case TranslateOutputMode.Array:
            {
                // The writer produces exactly one array file; it posts as one request holding the saved array.
                var file = savedFiles[0];
                var content = await ReadAllTextAsync(destination, file.Path, ct).ConfigureAwait(false);
                yield return new Batch(WrapArray(content, invoke.EnvelopeKey), file.Documents, Row: null, Label(file.Path));
                break;
            }

            case TranslateOutputMode.FilePerDocument:
            {
                if (invoke.BatchSize == 1)
                {
                    foreach (var file in savedFiles)
                    {
                        var content = await ReadAllTextAsync(destination, file.Path, ct).ConfigureAwait(false);
                        yield return new Batch(WrapDocuments([content], invoke), file.Documents, file.Row, Label(file.Path));
                    }

                    break;
                }

                var pending = new List<string>(invoke.BatchSize);
                long pendingDocuments = 0;
                string firstLabel = string.Empty;
                foreach (var file in savedFiles)
                {
                    if (pending.Count == 0)
                    {
                        firstLabel = Label(file.Path);
                    }

                    pending.Add(await ReadAllTextAsync(destination, file.Path, ct).ConfigureAwait(false));
                    pendingDocuments += file.Documents;
                    if (pending.Count == invoke.BatchSize)
                    {
                        yield return new Batch(WrapDocuments(pending, invoke), pendingDocuments, Row: null, $"{firstLabel} +{pending.Count - 1}");
                        pending.Clear();
                        pendingDocuments = 0;
                    }
                }

                if (pending.Count > 0)
                {
                    yield return new Batch(WrapDocuments(pending, invoke), pendingDocuments, Row: null, $"{firstLabel} +{pending.Count - 1}");
                }

                break;
            }

            case TranslateOutputMode.JsonLines:
            {
                var file = savedFiles[0];
                var pending = new List<string>(invoke.BatchSize);
                long first = 0;
                long line = 0;
                await foreach (var document in ReadLinesAsync(destination, file.Path, ct).ConfigureAwait(false))
                {
                    if (pending.Count == 0)
                    {
                        first = line + 1;
                    }

                    pending.Add(document);
                    line++;
                    if (pending.Count == invoke.BatchSize)
                    {
                        yield return new Batch(WrapDocuments(pending, invoke), pending.Count, Row: null, $"documents {first}-{line}");
                        pending.Clear();
                    }
                }

                if (pending.Count > 0)
                {
                    yield return new Batch(WrapDocuments(pending, invoke), pending.Count, Row: null, $"documents {first}-{line}");
                }

                break;
            }

            default:
                throw new SqlFlowException($"Unknown output mode '{mode}'.");
        }
    }

    /// <summary>Assembles one request body from saved document texts: a bare document for single-document
    /// batches, otherwise a JSON array, wrapped in the envelope object when one is declared (an envelope always
    /// wraps an array, so the endpoint sees one stable shape regardless of batch fill).</summary>
    private static string WrapDocuments(IReadOnlyList<string> documents, TranslateInvoke invoke)
    {
        if (invoke.EnvelopeKey is null && invoke.BatchSize == 1)
        {
            return documents[0];
        }

        var array = "[" + string.Join(",", documents) + "]";
        return WrapArray(array, invoke.EnvelopeKey);
    }

    private static string WrapArray(string arrayJson, string? envelopeKey)
        => envelopeKey is null ? arrayJson : $"{{{JsonSerializer.Serialize(envelopeKey)}:{arrayJson}}}";

    private static string Label(string path)
    {
        var index = path.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? path[(index + 1)..] : path;
    }

    private static async Task<string> ReadAllTextAsync(IExportDestination destination, string path, CancellationToken ct)
    {
        var stream = await destination.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        IExportDestination destination, string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = await destination.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Request composition
    // ---------------------------------------------------------------------------------------------------------

    private static string RenderUrl(string url, IReadOnlyDictionary<string, object?>? row)
    {
        var builder = new StringBuilder();
        foreach (var segment in TranslateTemplateText.Parse(url))
        {
            if (!segment.IsToken)
            {
                builder.Append(segment.Text);
                continue;
            }

            // The loader only admits URL tokens for per-document single-batches, so the row is present by
            // construction; a NULL value still has no URL form and must fail per document, loudly.
            if (row is null || !row.TryGetValue(segment.Text, out var value))
            {
                throw new SqlFlowException($"invoke.url references column '{segment.Text}', which the primary query does not return.");
            }

            if (value is null or DBNull)
            {
                throw new SqlFlowException($"invoke.url references column '{segment.Text}', which is NULL for this document.");
            }

            builder.Append(Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return builder.ToString();
    }

    private static string AppendQuery(string url, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0)
        {
            return url;
        }

        var suffix = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return url + (url.Contains('?', StringComparison.Ordinal) ? '&' : '?') + suffix;
    }

    private static HttpRequestMessage BuildRequest(
        string method,
        string url,
        IReadOnlyList<(string Name, string Value)> headers,
        IReadOnlyDictionary<string, string> authHeaders,
        string body,
        string contentType)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        foreach (var (name, value) in authHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Content = content;
        return request;
    }
}
