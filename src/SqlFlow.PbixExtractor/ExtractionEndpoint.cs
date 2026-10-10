using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.PbixExtractor;

/// <summary>
/// Bounds how many extractions run at once and how many wait for one. A request that would have to queue past the
/// limit is refused as busy immediately, so a burst of uploads degrades to "try again" instead of piling up
/// memory-hungry decompressions or holding connections open for minutes.
/// </summary>
public sealed class ExtractionGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly int _maxWaiting;
    private int _waiting;

    public ExtractionGate(IOptions<ExtractorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _slots = new SemaphoreSlim(options.Value.MaxConcurrent, options.Value.MaxConcurrent);
        _maxWaiting = options.Value.QueueLimit;
    }

    /// <summary>Takes a slot, waiting in line if the line is short enough. False when the service is too busy.</summary>
    public async Task<bool> TryEnterAsync(CancellationToken ct)
    {
        if (_slots.Wait(0, CancellationToken.None))
        {
            return true;
        }

        if (Interlocked.Increment(ref _waiting) > _maxWaiting)
        {
            Interlocked.Decrement(ref _waiting);
            return false;
        }

        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}

/// <summary>
/// <c>POST /v1/extract</c>: receives one <c>.pbix</c>, extracts it, and answers with its canonical specification. The
/// upload is written to a private temporary directory that is removed whatever happens, and only the specification
/// text ever leaves the process.
/// </summary>
public static partial class ExtractionEndpoint
{
    private const string LogCategory = "SqlFlow.PbixExtractor.Extraction";

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        string? reportFile,
        IReportExtractor extractor,
        ExtractionGate gate,
        IOptions<ExtractorOptions> options,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);
        var logger = loggers.CreateLogger(LogCategory);
        var settings = options.Value;

        if (!KeyMatches(http.Request.Headers[ReportExtractionProtocol.KeyHeader].ToString(), settings.ApiKey!))
        {
            return Results.Problem(
                detail: "The extractor key is missing or wrong.", statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized");
        }

        if (ReportSpecs.ReportFileProblem(reportFile) is { } labelProblem)
        {
            return Results.Problem(detail: labelProblem, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (http.Request.ContentLength > settings.MaxUploadBytes)
        {
            return TooLarge(settings);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = settings.MaxUploadBytes;
        }

        if (!await gate.TryEnterAsync(ct).ConfigureAwait(false))
        {
            http.Response.Headers.RetryAfter = "30";
            return Results.Problem(
                detail: "The extractor is busy with other reports; try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable, title: "Busy");
        }

        var directory = Directory.CreateTempSubdirectory("sqlflow-pbix-");
        var clock = Stopwatch.StartNew();
        try
        {
            var path = Path.Combine(directory.FullName, "report.pbix");
            long received;
            var file = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                received = await CopyBoundedAsync(http.Request.Body, file, settings.MaxUploadBytes, ct).ConfigureAwait(false);
            }

            if (received < 0)
            {
                return TooLarge(settings);
            }

            if (received == 0)
            {
                return Results.Problem(
                    detail: "The request carried no report.", statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid request");
            }

            var spec = await extractor.ExtractAsync(path, reportFile!, ct).ConfigureAwait(false);
            LogExtracted(logger, reportFile!, received, clock.ElapsedMilliseconds);
            return Results.Text(spec, ReportExtractionProtocol.SpecMediaType, Encoding.UTF8);
        }
        catch (PbixExtractException ex)
        {
            // The tool names the file it was given, which is this service's private temporary copy; the caller knows
            // the report by its label.
            var reason = ex.Message.Replace(Path.Combine(directory.FullName, "report.pbix"), reportFile!, StringComparison.Ordinal);
            LogRefused(logger, reportFile!, reason);
            return Results.Problem(
                detail: reason, statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The report could not be extracted");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(settings);
        }
        finally
        {
            gate.Release();
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temporary directory lives on the container's private scratch space, which is discarded with
                // the container; the failure is logged so a leak on a long-lived host is visible.
                LogCleanupFailed(logger, directory.FullName, ex.Message);
            }
        }
    }

    /// <summary>Copies at most <paramref name="max"/> bytes, returning how many were copied, or -1 when the source
    /// held more.</summary>
    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > max)
            {
                return -1;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return total;
    }

    private static bool KeyMatches(string presented, string expected)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    private static IResult TooLarge(ExtractorOptions settings)
        => Results.Problem(
            detail: $"The report is larger than {settings.MaxUploadMegabytes} MB.",
            statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large");

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted report '{ReportFile}' ({Bytes} bytes) in {ElapsedMs} ms.")]
    private static partial void LogExtracted(ILogger logger, string reportFile, long bytes, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused report '{ReportFile}': {Reason}")]
    private static partial void LogRefused(ILogger logger, string reportFile, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the temporary directory '{Directory}': {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string directory, string reason);
}
