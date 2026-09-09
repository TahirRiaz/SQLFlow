using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Sources;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The body to discover a source: a file or folder location the deployment can reach, plus the optional
/// format (blank = auto-detect), folder pattern/recursion, record-grain override, and sample bounds. The location is
/// scanned read-only; nothing is written to the catalog. <see cref="TraceSubject"/>, when supplied, streams the scan's
/// phases into the activity trace the GUI's bottom panel tails (keyed by this subject); blank runs the scan headless.</summary>
public sealed record SourceDiscoverRequest(
    string Location, string? Format, string? Pattern, bool? Recursive, string? RootPath,
    int? MaxFiles, int? MaxRecords, int? MaxDepth, string? DefaultColumnType, string? TraceSubject = null);

/// <summary>One discovered path (nested sources): its address, what it points at, the column it becomes, and how many
/// of the scanned records carried it (<see cref="Present"/> is false when it is missing from some records).</summary>
public sealed record DiscoveredPathDto(string Path, string Kind, string Column, int RecordCount, bool Present);

/// <summary>One discovered output column: its name, target SQL type, nullability, and (for a flattened nested source)
/// the source path it came from.</summary>
public sealed record DiscoveredColumnDto(string Name, string SqlType, bool Nullable, string? SourcePath);

/// <summary>A key/value pair for the generated <c>source.options</c> block.</summary>
public sealed record SourceOptionDto(string Key, string? Value);

/// <summary>The discovery outcome. <see cref="Mode"/> is "flatten" (JSON/XML: <see cref="Paths"/> + a record grain)
/// or "columnar" (CSV/Excel/Parquet: <see cref="Columns"/>). Both carry the runnable ingestion <see cref="GeneratedYaml"/>
/// and how the format was decided (<see cref="DetectionConfidence"/> + <see cref="DetectionEvidence"/>).</summary>
public sealed record SourceDiscoverResult(
    string Mode, string SourceType, string DetectionConfidence, IReadOnlyList<string> DetectionEvidence,
    string? AutoDetectedGrain, int FilesScanned, int RecordsScanned, bool SchemaDrift,
    IReadOnlyList<DiscoveredPathDto> Paths, IReadOnlyList<DiscoveredColumnDto> Columns,
    IReadOnlyList<SourceOptionDto> Options, string GeneratedYaml);

/// <summary>
/// Exposes unified source discovery over HTTP: given any supported file source (JSON, XML, CSV, Excel, Parquet) the
/// deployment can reach, detect its format, report its structure, and generate the ingestion flow YAML. Read-only, so
/// it maps under the same "operate" scope as datasource introspection and repo discover (it reads an external
/// location using the deployment's identity, but never mutates the catalog).
/// </summary>
public static class SourceDiscoverEndpoints
{
    // Sample bounds, clamped so a request cannot ask the node to walk an unbounded lake or parse to an unbounded
    // nesting depth. The defaults match the CLI's 'discover' verb (100 files, all records, depth 10).
    private const int DefaultMaxFiles = 100;
    private const int MaxAllowedFiles = 1000;
    private const int DefaultMaxRecords = 0;
    private const int MaxAllowedRecords = 1_000_000;
    private const int DefaultMaxDepth = 10;
    private const int MaxAllowedDepth = 50;

    // A discover subject is an ad-hoc location, not a fixed entity like a repo source, so distinct locations would
    // accumulate traces indefinitely. Per-subject pruning caps each location's scrollback; this age sweep bounds the
    // log across all locations, so exploring many different sources never grows the table without limit.
    private static readonly TimeSpan DiscoverTraceRetention = TimeSpan.FromDays(2);

    public static RouteGroupBuilder MapSourceDiscoverEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/sources/discover", DiscoverSourceAsync).WithTags("Sources").WithName("DiscoverSource");
        return group;
    }

    private static async Task<Results<Ok<SourceDiscoverResult>, ProblemHttpResult>> DiscoverSourceAsync(
        SourceDiscoverRequest request, SourceDiscoveryService discovery, CatalogDbContext catalog, TimeProvider clock,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Location))
        {
            return TypedResults.Problem(
                detail: "A discover requires a non-blank location (a file, folder, or URI the deployment can reach).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var serviceRequest = new SourceDiscoveryRequest(
            request.Location.Trim(),
            request.Format,
            string.IsNullOrWhiteSpace(request.Pattern) ? null : request.Pattern.Trim(),
            request.Recursive ?? false,
            request.RootPath,
            Clamp(request.MaxFiles ?? DefaultMaxFiles, 1, MaxAllowedFiles),
            Clamp(request.MaxRecords ?? DefaultMaxRecords, 0, MaxAllowedRecords),
            Clamp(request.MaxDepth ?? DefaultMaxDepth, 1, MaxAllowedDepth),
            string.IsNullOrWhiteSpace(request.DefaultColumnType) ? null : request.DefaultColumnType.Trim());

        // Optional live trace: when the caller supplies a subject, stream discovery phases into the activity log the
        // GUI's bottom panel tails (the same surface a repo sync uses), so an operator watches each phase happen and
        // sees exactly where a scan fails instead of only a terminal error toast. Blank subject runs headless.
        var logger = loggerFactory.CreateLogger("SqlFlow.ControlPlane.Api.SourceDiscover");
        ActivityTrace? trace = null;
        IDiscoveryProgress? progress = null;
        var subject = SubjectKey(request.TraceSubject);
        if (subject is not null)
        {
            await PruneDiscoverTracesAsync(catalog, clock, logger, ct).ConfigureAwait(false);
            trace = await ActivityTrace
                .BeginAsync(catalog, ActivityKinds.SourceDiscover, subject, clock, ct)
                .ConfigureAwait(false);
            progress = new ActivityTraceProgress(trace);
        }

        try
        {
            var result = await discovery.DiscoverAsync(serviceRequest, progress, ct).ConfigureAwait(false);

            var paths = result.Paths
                .Select(p => new DiscoveredPathDto(
                    p.Path,
                    p.Kind.ToString().ToLowerInvariant(),
                    p.Kind == SchemaPathKind.Container ? "-" : p.Column,
                    p.RecordCount,
                    p.RecordCount >= result.RecordsScanned))
                .ToList();

            var columns = result.Columns
                .Select(c => new DiscoveredColumnDto(c.Name, c.SqlType, c.Nullable, c.SourcePath))
                .ToList();

            var options = result.Options
                .Select(o => new SourceOptionDto(o.Key, o.Value))
                .ToList();

            if (trace is not null)
            {
                var shape = result.Mode == "flatten"
                    ? $"{result.RecordsScanned} record(s), {paths.Count} path(s)"
                    : $"{columns.Count} column(s)";
                await trace.CompleteAsync(
                    ActivityStatuses.Succeeded,
                    $"Discovery complete: {result.SourceType} ({result.FilesScanned} file(s), {shape}).", ct)
                    .ConfigureAwait(false);
            }

            return TypedResults.Ok(new SourceDiscoverResult(
                result.Mode, result.SourceType, result.DetectionConfidence, result.DetectionEvidence,
                result.AutoDetectedGrain, result.FilesScanned, result.RecordsScanned, result.SchemaDrift,
                paths, columns, options, result.GeneratedYaml));
        }
        catch (SqlFlowException ex)
        {
            // A bad location, an empty selection, an unknown format, or a malformed sample are the caller's to fix, so
            // they surface as a 400 with a clean (secret-redacted) message rather than an opaque 500.
            var redacted = SecretHygiene.RedactedMessage(ex);
            await CompleteFailureAsync(trace, redacted, logger).ConfigureAwait(false);
            return TypedResults.Problem(
                detail: redacted, statusCode: StatusCodes.Status400BadRequest, title: "Discover failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client walked away (tab closed, navigation). Land a terminal line so a reopened panel is not left
            // tailing a never-ending activity, then let the cancellation propagate.
            await CompleteFailureAsync(trace, "Discovery was cancelled.", logger).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // An unexpected fault still becomes a 500, but the panel must not hang: land a terminal failure first.
            await CompleteFailureAsync(trace, SecretHygiene.RedactedMessage(ex), logger).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Best-effort terminal "failed" line on a short independent deadline, so a cancelled or unhealthy request
    /// context cannot leave the panel tailing an activity that never ends (mirrors the repo-sync trace's failure path).</summary>
    private static async Task CompleteFailureAsync(ActivityTrace? trace, string message, ILogger logger)
    {
        if (trace is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await trace.CompleteAsync(ActivityStatuses.Failed, $"Discovery failed: {message}", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The terminal line is best-effort; the age sweep and the next discover's pruning reclaim a dangling
            // activity. Log rather than mask the original fault this catch sits inside.
            logger.LogWarning(ex, "Could not write the discover trace's terminal failure line.");
        }
    }

    /// <summary>Deletes discover-trace events older than the retention window, keeping the shared activity log bounded
    /// no matter how many distinct locations are explored.</summary>
    private static async Task PruneDiscoverTracesAsync(CatalogDbContext catalog, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            var cutoff = clock.GetUtcNow().UtcDateTime - DiscoverTraceRetention;
            await catalog.ActivityEvents
                .Where(e => e.Kind == ActivityKinds.SourceDiscover && e.TimestampUtc < cutoff)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retention is housekeeping, not part of the discover's contract: a sweep failure must not fail the scan.
            logger.LogWarning(ex, "Could not prune expired discover traces.");
        }
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    // The ActivityEvent.SubjectKey column is capped at 256, and a location URI can exceed that, so trim to the same
    // bound the GUI applies (it keys the panel by the same value). Null/blank means the caller opted out of tracing.
    private const int MaxSubjectLength = 256;

    private static string? SubjectKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return trimmed.Length <= MaxSubjectLength ? trimmed : trimmed[..MaxSubjectLength];
    }

    /// <summary>Forwards each discovery phase into the activity trace as an info line (the GUI panel tails it live).</summary>
    private sealed class ActivityTraceProgress(ActivityTrace trace) : IDiscoveryProgress
    {
        public Task ReportAsync(string phase, string message, CancellationToken ct = default)
            => trace.InfoAsync(phase, message, ct);
    }
}
