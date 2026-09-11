using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch.Tests;

/// <summary>A clock the tests move by hand, so lease expiry, task expiry and reconcile cadences are exercised
/// deterministically instead of by sleeping.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset start) => _now = start;

    public static ManualClock At(int year = 2026, int month = 9, int day = 11) => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// An in-memory journal with the exact semantics the catalog stores implement: conditional writes fenced on
/// (node, attempt), skip-dependents on a non-success terminal, and the same status vocabulary. It records every
/// call so a test can assert what was journaled, and it can be told to fail or delay the next write to exercise
/// the dispatcher's recovery paths.
/// </summary>
internal sealed class FakeLedger : IDispatchLedger
{
    public sealed class RunRow
    {
        public required DispatchRun Placement { get; set; }

        public string Status { get; set; } = "queued";

        public string? Node { get; set; }

        public int Attempt { get; set; }

        public bool CancelRequested { get; set; }

        public string? Error { get; set; }

        public string? ArtifactJson { get; set; }

        /// <summary>The member ids that must be skipped when this run ends unsuccessfully (the test's stand-in for
        /// the lineage walk the catalog store performs).</summary>
        public List<Guid> Dependents { get; } = [];
    }

    public sealed class TaskRow
    {
        public required DispatchTask Placement { get; set; }

        public string Status { get; set; } = "queued";

        public string? Node { get; set; }

        public bool CancelRequested { get; set; }

        public string? Error { get; set; }

        public string? ResultJson { get; set; }

        public DateTime? StartUtc { get; set; }
    }

    public ConcurrentDictionary<Guid, RunRow> Runs { get; } = new();

    public ConcurrentDictionary<Guid, TaskRow> Tasks { get; } = new();

    public ConcurrentDictionary<string, (NodeHeartbeat Beat, DateTime? Restart)> Nodes { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Calls { get; } = new();

    public string? LeaseOwner { get; private set; }

    public DateTime LeaseExpiresUtc { get; private set; }

    public long LeaseEpoch { get; private set; }

    /// <summary>When set, the next journal write throws this and then clears it.</summary>
    public Exception? FailNextWrite { get; set; }

    /// <summary>When set, every write waits on this before proceeding (to hold a hand-out mid-journal).</summary>
    public SemaphoreSlim? HoldWrites { get; set; }

    public int WriteCount;

    private readonly Lock _gate = new();

    public RunRow AddQueued(DispatchRun run)
    {
        var row = new RunRow { Placement = run, Attempt = run.Attempt, CancelRequested = run.CancelRequested };
        Runs[run.RunId] = row;
        return row;
    }

    public RunRow AddRunning(DispatchRun run, string node)
    {
        var row = new RunRow { Placement = run, Attempt = run.Attempt, Status = "running", Node = node, CancelRequested = run.CancelRequested };
        Runs[run.RunId] = row;
        return row;
    }

    public TaskRow AddQueuedTask(DispatchTask task)
    {
        var row = new TaskRow { Placement = task, CancelRequested = task.CancelRequested };
        Tasks[task.TaskId] = row;
        return row;
    }

    private async Task WriteGateAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref WriteCount);
        if (HoldWrites is { } hold)
        {
            await hold.WaitAsync(ct);
        }

        var fail = FailNextWrite;
        if (fail is not null)
        {
            FailNextWrite = null;
            throw fail;
        }
    }

    public Task<DispatchLedgerSnapshot> LoadAsync(CancellationToken ct)
    {
        Calls.Enqueue("load");
        lock (_gate)
        {
            var queuedRuns = Runs.Values.Where(r => r.Status == "queued").Select(r => Current(r)).ToList();
            var runningRuns = Runs.Values.Where(r => r.Status == "running" && r.Node is not null)
                .Select(r => new RunningRunRecord(Current(r), r.Node!)).ToList();
            var queuedTasks = Tasks.Values.Where(t => t.Status == "queued").Select(t => t.Placement).ToList();
            var runningTasks = Tasks.Values.Where(t => t.Status == "running" && t.Node is not null)
                .Select(t => new RunningTaskRecord(t.Placement, t.Node!)).ToList();
            return Task.FromResult(new DispatchLedgerSnapshot(queuedRuns, runningRuns, queuedTasks, runningTasks));
        }
    }

    private static DispatchRun Current(RunRow row) => row.Placement with { Attempt = row.Attempt, CancelRequested = row.CancelRequested };

    public Task<DispatchActiveIds> ListActiveAsync(CancellationToken ct)
    {
        Calls.Enqueue("list-active");
        lock (_gate)
        {
            return Task.FromResult(new DispatchActiveIds(
                Runs.Values.Where(r => r.Status == "queued").Select(r => r.Placement.RunId).ToList(),
                Runs.Values.Where(r => r.Status == "running").Select(r => new ActiveRunRef(r.Placement.RunId, r.Node, r.Attempt, r.CancelRequested)).ToList(),
                Tasks.Values.Where(t => t.Status == "queued").Select(t => t.Placement.TaskId).ToList(),
                Tasks.Values.Where(t => t.Status == "running").Select(t => new ActiveTaskRef(t.Placement.TaskId, t.Node, t.CancelRequested)).ToList()));
        }
    }

    public Task<IReadOnlyList<DispatchRun>> LoadQueuedRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<DispatchRun> rows = runIds
                .Select(id => Runs.GetValueOrDefault(id))
                .Where(r => r is { Status: "queued" })
                .Select(r => Current(r!))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<DispatchTask>> LoadQueuedTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<DispatchTask> rows = taskIds
                .Select(id => Tasks.GetValueOrDefault(id))
                .Where(t => t is { Status: "queued" })
                .Select(t => t!.Placement)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<RunningRunRecord>> LoadRunningRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RunningRunRecord> rows = runIds
                .Select(id => Runs.GetValueOrDefault(id))
                .Where(r => r is { Status: "running", Node: not null })
                .Select(r => new RunningRunRecord(Current(r!), r!.Node!))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<RunningTaskRecord>> LoadRunningTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<RunningTaskRecord> rows = taskIds
                .Select(id => Tasks.GetValueOrDefault(id))
                .Where(t => t is { Status: "running", Node: not null })
                .Select(t => new RunningTaskRecord(t!.Placement, t!.Node!))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public async Task<bool> MarkRunHandedOutAsync(Guid runId, int expectedAttempt, string node, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"handout:{runId}:{node}:{expectedAttempt}");
        lock (_gate)
        {
            if (!Runs.TryGetValue(runId, out var row) || row.Status != "queued" || row.Attempt != expectedAttempt)
            {
                return false;
            }

            row.Status = "running";
            row.Node = node;
            row.Attempt = expectedAttempt + 1;
            return true;
        }
    }

    public async Task<RunOutcomeRecord> RecordRunOutcomeAsync(
        Guid runId, string node, int attempt, RunOutcomeKind outcome, string? failure, string? artifactJson,
        DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"outcome:{runId}:{node}:{attempt}:{outcome}");
        lock (_gate)
        {
            if (!Runs.TryGetValue(runId, out var row) || row.Status != "running" || row.Node != node || row.Attempt != attempt)
            {
                return new RunOutcomeRecord(RunOutcomeStatus.StaleClaim, []);
            }

            bool success;
            var status = RunOutcomeStatus.Recorded;
            switch (outcome)
            {
                case RunOutcomeKind.Completed when string.IsNullOrWhiteSpace(artifactJson) || artifactJson.Contains("\"corrupt\"", StringComparison.Ordinal):
                    success = false;
                    status = RunOutcomeStatus.ArtifactUnreadable;
                    row.Error = "the run executed but its result could not be recorded.";
                    break;
                case RunOutcomeKind.Completed:
                    success = artifactJson.Contains("\"success\": true", StringComparison.Ordinal);
                    row.ArtifactJson = artifactJson;
                    break;
                case RunOutcomeKind.Failed:
                    success = false;
                    row.Error = failure;
                    break;
                default:
                    success = false;
                    row.Error = "cancelled";
                    break;
            }

            row.Status = outcome == RunOutcomeKind.Cancelled ? "cancelled" : success ? "succeeded" : "failed";
            return new RunOutcomeRecord(status, success ? [] : SkipDependents(row));
        }
    }

    private List<Guid> SkipDependents(RunRow row)
    {
        var skipped = new List<Guid>();
        foreach (var dependent in row.Dependents)
        {
            if (Runs.TryGetValue(dependent, out var d) && d.Status == "queued")
            {
                d.Status = "skipped";
                skipped.Add(dependent);
            }
        }

        return skipped;
    }

    public async Task<InterruptedRunRecord> RequeueInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"requeue:{runId}:{node}:{attempt}");
        lock (_gate)
        {
            if (!Runs.TryGetValue(runId, out var row) || row.Status != "running" || row.Node != node || row.Attempt != attempt || row.CancelRequested)
            {
                return new InterruptedRunRecord(false, []);
            }

            row.Status = "queued";
            row.Node = null;
            return new InterruptedRunRecord(true, []);
        }
    }

    public async Task<InterruptedRunRecord> FailInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"fail-interrupted:{runId}:{node}:{attempt}");
        lock (_gate)
        {
            if (!Runs.TryGetValue(runId, out var row) || row.Status != "running" || row.Node != node || row.Attempt != attempt)
            {
                return new InterruptedRunRecord(false, []);
            }

            row.Status = "failed";
            row.Error = "interrupted";
            return new InterruptedRunRecord(true, SkipDependents(row));
        }
    }

    public async Task<InterruptedRunRecord> CancelInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"cancel-interrupted:{runId}:{node}:{attempt}");
        lock (_gate)
        {
            if (!Runs.TryGetValue(runId, out var row) || row.Status != "running" || row.Node != node || row.Attempt != attempt)
            {
                return new InterruptedRunRecord(false, []);
            }

            row.Status = "cancelled";
            return new InterruptedRunRecord(true, SkipDependents(row));
        }
    }

    public async Task<bool> MarkTaskHandedOutAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"task-handout:{taskId}:{node}");
        lock (_gate)
        {
            if (!Tasks.TryGetValue(taskId, out var row) || row.Status != "queued")
            {
                return false;
            }

            row.Status = "running";
            row.Node = node;
            row.StartUtc = nowUtc;
            return true;
        }
    }

    public async Task<bool> RecordTaskOutcomeAsync(
        Guid taskId, string node, TaskOutcomeKind outcome, string? failure, string? resultJson, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"task-outcome:{taskId}:{node}:{outcome}");
        lock (_gate)
        {
            if (!Tasks.TryGetValue(taskId, out var row) || row.Status != "running" || row.Node != node)
            {
                return false;
            }

            row.Status = outcome switch
            {
                TaskOutcomeKind.Succeeded => "succeeded",
                TaskOutcomeKind.Failed => "failed",
                _ => "cancelled",
            };
            row.ResultJson = resultJson;
            row.Error = failure;
            return true;
        }
    }

    public async Task<bool> RequeueInterruptedTaskAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct)
    {
        await WriteGateAsync(ct);
        Calls.Enqueue($"task-requeue:{taskId}:{node}");
        lock (_gate)
        {
            if (!Tasks.TryGetValue(taskId, out var row) || row.Status != "running" || row.Node != node)
            {
                return false;
            }

            if (row.CancelRequested)
            {
                row.Status = "cancelled";
                return false;
            }

            row.Status = "queued";
            row.Node = null;
            row.StartUtc = null;
            return true;
        }
    }

    public Task<IReadOnlyList<Guid>> ExpireTasksAsync(DateTime queuedBefore, DateTime runningBefore, DateTime nowUtc, CancellationToken ct)
    {
        Calls.Enqueue("expire-tasks");
        lock (_gate)
        {
            var expired = new List<Guid>();
            foreach (var row in Tasks.Values)
            {
                if ((row.Status == "queued" && row.Placement.EnqueuedUtc < queuedBefore)
                    || (row.Status == "running" && row.StartUtc is { } start && start < runningBefore))
                {
                    row.Status = "failed";
                    row.Error = "expired";
                    expired.Add(row.Placement.TaskId);
                }
            }

            return Task.FromResult<IReadOnlyList<Guid>>(expired);
        }
    }

    public Task<DateTime?> RecordNodeHeartbeatAsync(NodeHeartbeat heartbeat, CancellationToken ct)
    {
        Calls.Enqueue($"heartbeat:{heartbeat.Name}");
        var restart = Nodes.TryGetValue(heartbeat.Name, out var existing) ? existing.Restart : null;
        Nodes[heartbeat.Name] = (heartbeat, restart);
        return Task.FromResult(restart);
    }

    public void RequestRestart(string node, DateTime requestedUtc)
    {
        var beat = Nodes.TryGetValue(node, out var existing) ? existing.Beat : new NodeHeartbeat(node, null, null, 0, requestedUtc);
        Nodes[node] = (beat, requestedUtc);
    }

    public Task<int> PruneNodesAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        var stale = Nodes.Where(n => n.Value.Beat.LastSeenUtc < olderThanUtc).Select(n => n.Key).ToList();
        foreach (var name in stale)
        {
            Nodes.TryRemove(name, out _);
        }

        return Task.FromResult(stale.Count);
    }

    public Task<bool> TryAcquireOwnershipAsync(string owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (LeaseOwner is null || LeaseOwner == owner || LeaseExpiresUtc < nowUtc)
            {
                if (LeaseOwner != owner)
                {
                    LeaseEpoch++;
                }

                LeaseOwner = owner;
                LeaseExpiresUtc = nowUtc + ttl;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task ReleaseOwnershipAsync(string owner, CancellationToken ct)
    {
        lock (_gate)
        {
            if (LeaseOwner == owner)
            {
                LeaseExpiresUtc = DateTime.MinValue;
            }

            return Task.CompletedTask;
        }
    }
}

internal static class Wait
{
    /// <summary>Polls a condition until it holds or the timeout elapses, so a test never sleeps a fixed interval
    /// and hopes the background work has happened.</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition did not hold within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}

internal static class Make
{
    public static DispatchOptions Options(int leaseSeconds = 90, int longPollSeconds = 30, int reconcileSeconds = 5)
        => new() { LeaseSeconds = leaseSeconds, LongPollSeconds = longPollSeconds, ReconcileSeconds = reconcileSeconds };

    public static Dispatcher Dispatcher(FakeLedger ledger, ManualClock clock, DispatchOptions? options = null)
        => new(ledger, options ?? Options(), clock, NullLogger<Dispatcher>.Instance);

    public static async Task<Dispatcher> ActiveDispatcherAsync(FakeLedger ledger, ManualClock clock, DispatchOptions? options = null)
    {
        var dispatcher = Dispatcher(ledger, clock, options);
        await dispatcher.ActivateAsync("test-owner", CancellationToken.None);
        return dispatcher;
    }

    public static DispatchRun Run(
        DateTime enqueuedUtc, Guid? pipeline = null, string? pool = null, Guid? group = null, int wave = 0,
        int? cap = null, int attempt = 0)
        => new(Guid.CreateVersion7(), pipeline ?? Guid.NewGuid(), pool, group, wave, cap, enqueuedUtc, attempt, false);

    public static DispatchTask Task(DateTime enqueuedUtc, string? pool = null)
        => new(Guid.CreateVersion7(), pool, enqueuedUtc, false);

    public static NodePollRequest Poll(
        string node, int freeRuns = 1, IReadOnlyList<string>? pools = null, IReadOnlyList<HeldRun>? holding = null,
        int waitSeconds = 0, int freeTasks = 0, IReadOnlyList<Guid>? holdingTasks = null, DateTime? startedUtc = null,
        int runSlots = 4)
        => new(node, "test", pools ?? [], runSlots, freeRuns, 2, freeTasks, holding ?? [], holdingTasks ?? [],
            startedUtc ?? new DateTime(2026, 9, 11, 11, 0, 0, DateTimeKind.Utc), waitSeconds);

    public const string Artifact = """{ "schemaVersion": 1, "success": true }""";

    public const string FailedArtifact = """{ "schemaVersion": 1, "success": false }""";
}
