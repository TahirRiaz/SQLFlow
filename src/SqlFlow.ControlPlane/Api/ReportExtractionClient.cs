using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.ControlPlane.Api;

/// <summary>What asking the extractor produced: the specification, or why there is none.</summary>
/// <param name="Spec">The specification text the extractor answered with, still to be validated; null on failure.</param>
/// <param name="StatusCode">The status to answer the caller with when <paramref name="Spec"/> is null.</param>
/// <param name="Problem">Why there is no specification.</param>
public sealed record ReportExtractionOutcome(string? Spec, int StatusCode, string? Problem);

/// <summary>
/// Forwards an uploaded <c>.pbix</c> to the isolated extractor service and brings back its answer. The bytes are
/// streamed straight through, never written or parsed here, and what comes back is treated as untrusted: the caller
/// validates it with <see cref="ReportSpecs.Normalize"/> before anything is stored.
/// </summary>
public sealed class ReportExtractionClient
{
    public const string HttpClientName = "pbix-extractor";

    private readonly IHttpClientFactory _http;
    private readonly ReportExtractionOptions _options;
    private readonly ISecretResolver _secrets;

    public ReportExtractionClient(IHttpClientFactory http, IOptions<ControlPlaneOptions> options, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        _http = http;
        _options = options.Value.PowerAI.ReportExtraction;
        _secrets = secrets;
    }

    public bool IsEnabled => _options.Enabled;

    public long MaxUploadBytes => _options.MaxUploadBytes;

    /// <summary>Sends <paramref name="report"/> to the extractor, reading at most <see cref="MaxUploadBytes"/> of it.</summary>
    public async Task<ReportExtractionOutcome> ExtractAsync(
        Stream report, string reportFile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportFile);

        if (!_options.Enabled)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status501NotImplemented,
                "Report extraction is not enabled on this control plane (ControlPlane:PowerAI:ReportExtraction:Enabled). "
                + "Upload a specification made with 'sqlflow powerbi extract' instead.");
        }

        string key;
        try
        {
            key = await _secrets.ResolveAsync(_options.ApiKey ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (Core.SqlFlowException ex)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status503ServiceUnavailable,
                $"The extractor key could not be resolved ({SecretHygiene.RedactedMessage(ex)}).");
        }

        if (string.IsNullOrEmpty(key))
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status503ServiceUnavailable,
                "The extractor key (ControlPlane:PowerAI:ReportExtraction:ApiKey) did not resolve to a value.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var bounded = new BoundedReadStream(report, _options.MaxUploadBytes);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(_options.Endpoint!), $"{ReportExtractionProtocol.ExtractPath}?reportFile={Uri.EscapeDataString(reportFile)}"));
        request.Headers.Add(ReportExtractionProtocol.KeyHeader, key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ReportExtractionProtocol.SpecMediaType));
        request.Content = new StreamContent(bounded);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var client = _http.CreateClient(HttpClientName);
        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
            var body = await ReadBoundedAsync(response.Content, budget.Token).ConfigureAwait(false);
            if (body is null)
            {
                return new ReportExtractionOutcome(
                    null, StatusCodes.Status502BadGateway, "The extractor answered with more text than a specification may hold.");
            }

            if (response.IsSuccessStatusCode)
            {
                return new ReportExtractionOutcome(body, StatusCodes.Status200OK, null);
            }

            return new ReportExtractionOutcome(null, MapStatus(response.StatusCode), ProblemDetail(body, response.StatusCode));
        }
        catch (UploadTooLargeException)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status413PayloadTooLarge,
                $"The report is larger than {_options.MaxUploadMegabytes} MB.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status504GatewayTimeout,
                $"The extractor did not answer within {_options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is UploadTooLargeException)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status413PayloadTooLarge,
                $"The report is larger than {_options.MaxUploadMegabytes} MB.");
        }
        catch (HttpRequestException ex)
        {
            return new ReportExtractionOutcome(
                null, StatusCodes.Status503ServiceUnavailable,
                $"The extractor could not be reached ({SecretHygiene.RedactedMessage(ex)}).");
        }
    }

    /// <summary>The extractor's own refusals are passed on as they are; anything else it answers is its failure,
    /// not the caller's.</summary>
    private static int MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
        HttpStatusCode.RequestEntityTooLarge => StatusCodes.Status413PayloadTooLarge,
        HttpStatusCode.UnprocessableEntity => StatusCodes.Status422UnprocessableEntity,
        HttpStatusCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway,
    };

    private static string ProblemDetail(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && detail.GetString() is { Length: > 0 } text)
            {
                return SecretHygiene.RedactedMessage(text.Length <= 1000 ? text : text[..1000] + "...");
            }
        }
        catch (JsonException)
        {
            // Not a problem document; the status alone is reported.
        }

        return $"The extractor answered {(int)status} {status}.";
    }

    /// <summary>Reads the answer, refusing one larger than a specification may be. Null when it is.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > ReportSpecs.MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    private sealed class UploadTooLargeException : IOException
    {
        public UploadTooLargeException()
            : base("The upload exceeded the configured limit.")
        {
        }

        public UploadTooLargeException(string message)
            : base(message)
        {
        }

        public UploadTooLargeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>A read-only pass-through that fails once more than the limit has been read, so an oversized upload is
    /// cut off while it streams rather than after it has all been sent on.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new UploadTooLargeException() : read;
        }
    }
}
