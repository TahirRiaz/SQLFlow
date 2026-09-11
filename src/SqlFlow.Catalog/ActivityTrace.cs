using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Runs;

namespace SqlFlow.Catalog;

/// <summary>
/// Writes a control-plane <em>activity</em> trace: the general-purpose, reusable counterpart to a run's live
/// trace feed (<see cref="RunTraceStore.AppendLiveAsync"/>). Any long-running operation (a repository sync, a
/// lineage computation, and so on)
/// constructs one of these for its (<c>kind</c>, <c>subjectKey</c>) and calls
/// <see cref="InfoAsync"/>/<see cref="WarnAsync"/>/<see cref="ErrorAsync"/> as it progresses, then
/// <see cref="CompleteAsync"/> at its terminal outcome. Each call appends one <see cref="CatalogActivityEvent"/>
/// row under a monotonic id and a shared <see cref="CatalogActivityEvent.ActivityId"/>, so the GUI's bottom trace
/// panel tails the operation live exactly as it tails a run's trace, giving the operator insight into what is
/// happening instead of only a terminal status.
/// </summary>
/// <remarks>
/// Coarse and synchronous by design (an operation emits a handful of phase lines, not a high-frequency stream), so
/// no channel/background writer is needed. It writes on the caller's <see cref="CatalogDbContext"/> and detaches
/// each row it saves, so it never disturbs entities the operation itself is tracking on that context. Best-effort
/// is the caller's choice: a failed trace write throws like any DB call and the caller decides whether tracing is
/// allowed to fail the operation (for a sync it is not - see <c>RepoSyncService</c>).
/// </remarks>
public sealed class ActivityTrace
{
    /// <summary>How many recent activities to retain per (kind, subject); older ones are pruned when a new one
    /// begins, so the log stays bounded while keeping a short scrollback of previous attempts.</summary>
    private const int RetainActivities = 5;

    private readonly CatalogDbContext _context;
    private readonly string _kind;
    private readonly string _subjectKey;
    private readonly TimeProvider _clock;
    private int _ordinal;

    private ActivityTrace(CatalogDbContext context, string kind, string subjectKey, Guid activityId, TimeProvider clock)
    {
        _context = context;
        _kind = kind;
        _subjectKey = subjectKey;
        ActivityId = activityId;
        _clock = clock;
    }

    /// <summary>This activity's correlation id (all its events share it).</summary>
    public Guid ActivityId { get; }

    /// <summary>
    /// Starts a new activity for (<paramref name="kind"/>, <paramref name="subjectKey"/>): prunes the subject's
    /// older activities down to the retention cap, then returns a writer whose first appended event is ordinal 1.
    /// </summary>
    public static async Task<ActivityTrace> BeginAsync(
        CatalogDbContext context, string kind, string subjectKey, TimeProvider clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKey);
        ArgumentNullException.ThrowIfNull(clock);

        // Keep only the newest few existing activities for this subject (by their highest event id); the new one
        // this writer is about to append survives because it is not yet present. Bounded scan on the (kind,
        // subject, id) index.
        var keep = await context.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == kind && e.SubjectKey == subjectKey)
            .GroupBy(e => e.ActivityId)
            .Select(g => new { ActivityId = g.Key, MaxId = g.Max(e => e.Id) })
            .OrderByDescending(x => x.MaxId)
            .Take(RetainActivities - 1)
            .Select(x => x.ActivityId)
            .ToListAsync(ct).ConfigureAwait(false);

        await context.ActivityEvents
            .Where(e => e.Kind == kind && e.SubjectKey == subjectKey && !keep.Contains(e.ActivityId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return new ActivityTrace(context, kind, subjectKey, Guid.NewGuid(), clock);
    }

    public Task InfoAsync(string step, string message, CancellationToken ct = default)
        => AppendAsync(step, RunEventLevels.Info, message, terminal: false, status: null, ct);

    public Task WarnAsync(string step, string message, CancellationToken ct = default)
        => AppendAsync(step, RunEventLevels.Warning, message, terminal: false, status: null, ct);

    public Task ErrorAsync(string step, string message, CancellationToken ct = default)
        => AppendAsync(step, RunEventLevels.Error, message, terminal: false, status: null, ct);

    /// <summary>Appends the terminal event that carries the activity's outcome; the trace stream ends once it lands
    /// (and no newer activity for the subject is pending).</summary>
    public Task CompleteAsync(string status, string message, CancellationToken ct = default)
    {
        var level = string.Equals(status, ActivityStatuses.Failed, StringComparison.Ordinal)
            ? RunEventLevels.Error
            : RunEventLevels.Info;
        return AppendAsync("done", level, message, terminal: true, status: status, ct);
    }

    private async Task AppendAsync(string? step, string level, string message, bool terminal, string? status, CancellationToken ct)
    {
        _ordinal++;
        var row = new CatalogActivityEvent
        {
            ActivityId = ActivityId,
            Kind = _kind,
            SubjectKey = _subjectKey,
            Ordinal = _ordinal,
            TimestampUtc = _clock.GetUtcNow().UtcDateTime,
            Level = level,
            Step = step,
            Message = message,
            Terminal = terminal,
            Status = status,
        };
        _context.ActivityEvents.Add(row);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        // Detach only our row, so a caller tracking its own entities on this context is undisturbed.
        _context.Entry(row).State = EntityState.Detached;
    }
}

/// <summary>The terminal outcomes an <see cref="ActivityTrace"/> completes with.</summary>
public static class ActivityStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>The well-known <see cref="CatalogActivityEvent.Kind"/> values, so producers and the GUI agree.</summary>
public static class ActivityKinds
{
    /// <summary>A managed git repository sync (clone + catalog reconcile). Subject: the repo source id.</summary>
    public const string RepoSync = "repo-sync";

    /// <summary>A source discovery scan (format detection, delimiter sniff, schema read, YAML generation). Subject:
    /// the scanned location, so repeat discoveries of the same source keep a short bounded scrollback.</summary>
    public const string SourceDiscover = "source-discover";
}
