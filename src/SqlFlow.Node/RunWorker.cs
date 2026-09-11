using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Node;

/// <summary>How a node runs: the pools it serves, how much it executes at once, how long it parks a poll, how long
/// a stop lets in-flight work finish, and what an operator restart request should do to the host.</summary>
public sealed class RunWorkerOptions
{
    /// <summary>The pools this node serves alongside untargeted runs (an empty list = untargeted only).</summary>
    public IReadOnlyList<string> Pools { get; init; } = [];

    /// <summary>How many runs execute at once on this node (minimum 1).</summary>
    public int MaxConcurrentRuns { get; init; } = RunWorker.DefaultMaxConcurrentRuns;

    /// <summary>How many compute tasks execute at once on this node (minimum 1).</summary>
    public int MaxConcurrentComputeTasks { get; init; } = RunWorker.DefaultMaxConcurrentComputeTasks;

    /// <summary>How long each poll asks the dispatcher to hold it when nothing is available. Also the node's heartbeat
    /// cadence while saturated, so it must stay well inside the dispatcher's lease.</summary>
    public TimeSpan PollWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a stopping node keeps executing the runs it already holds before severing them; zero severs
    /// at once. Keep it under the orchestrator's termination grace period.</summary>
    public TimeSpan DrainTimeout { get; init; } = RunWorker.DefaultDrainTimeout;

    /// <summary>Invoked once when the dispatcher relays an operator's restart request: the host wires it to a
    /// graceful stop (drain, then exit) so the orchestrator recreates the replica. Null means restarts are ignored.</summary>
    public Func<CancellationToken, Task>? OnRestartRequested { get; init; }
}

/// <summary>
/// The compute-node runtime: it polls the dispatcher for work through an <see cref="INodeTransport"/> (in-process on
/// the control plane's own node, HTTP from a standalone <c>sqlflow worker</c>), executes up to a bounded number of
/// handed-out runs concurrently, each on its own DI scope, through the shared <see cref="DocumentExecutor"/>
/// (identical to a CLI run), and reports each outcome back under the hand-out's fence. The dispatcher owns
/// placement; the node owns execution and never touches the queue. Each node resolves every credential from its own
/// environment, so nothing sensitive travels through the protocol.
/// </summary>
/// <remarks>
/// Robustness: one run's failure never tears down the loop or its sibling runs (per-run try/catch drives the run
/// to a terminal outcome report and continues); a poll error (the control plane unreachable, a passive replica)
/// is logged and retried with jittered backoff. Every poll renews the leases of what the node holds; a run whose
/// lease the dispatcher revoked (it lapsed and was requeued) is aborted at once, since another node may be executing
/// it. On shutdown the node stops polling for work and lets the in-flight runs finish, still polling to keep their
/// leases alive, or, if the drain window expires, severs them and lets the dispatcher requeue them.
/// </remarks>
public sealed partial class RunWorker : IDisposable
{
    /// <summary>The default bound on how many handed-out runs a node executes at once.</summary>
    public const int DefaultMaxConcurrentRuns = 4;

    /// <summary>The default bound on how many COMPUTE TASKS a node executes at once. Deliberately its own small
    /// gate, separate from the run gate: compute tasks are interactive (an operator browsing a datasource in the
    /// GUI), so a node saturated with long flow runs must still answer them promptly, and a burst of browsing must
    /// never starve flow execution of its slots.</summary>
    public const int DefaultMaxConcurrentComputeTasks = 2;

    /// <summary>How long a stopping node keeps executing the work it has ALREADY been handed before severing it. A
    /// stop is routine and frequent in an autoscaled fleet - the scaler reclaims a replica, a revision swaps, an
    /// operator restarts a node - and it means "stop taking work and finish what you hold", never "drop it".
    /// Severing a run records no outcome at all, so it is recovered only by the dispatcher's lease expiry, which
    /// consumes one of the run's <see cref="RunQueueStore.MaxExecutionAttempts"/> executions and repeats all of its
    /// work; a run unlucky enough to be caught by three stops is then failed outright and blamed for dying, though
    /// nothing was ever wrong with it. Draining is what keeps a scale-in from manufacturing those failures. The
    /// default sits under the ten-minute termination grace the worker's container app declares, so the drain ends
    /// on the node's own terms with an outcome recorded, rather than being cut off mid-statement by the platform's
    /// kill.</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromMinutes(9);

    /// <summary>How often the drain re-polls (renewing leases, observing cancels) while it waits.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>The floor and ceiling of the jittered backoff between failed polls.</summary>
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly INodeTransport _transport;
    private readonly DocumentExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunWorker> _logger;
    private readonly string _node = Environment.MachineName;
    private readonly string? _version = typeof(RunWorker).Assembly.GetName().Version?.ToString();
    private readonly GitMaterializer _materializer = new();

    // The pools this node serves, set once in RunAsync and reported on every poll.
    private IReadOnlyList<string> _pools = [];

    // The moment this incarnation started, stamped once in RunAsync and reported on every poll so the dispatcher
    // relays only restart requests newer than it: a stale request left on the node row never bounces the
    // replacement, and a same-name restart can never loop.
    private DateTime _startedUtc;

    private Func<CancellationToken, Task>? _onRestartRequested;

    // Set the first time a restart is initiated so the stop fires exactly once, even across the several polls that
    // may elapse while the drain completes.
    private int _restartInitiated;

    // The runs this node is executing, keyed by run id: each with its own cancellation source (linked to the abort
    // token) and the attempt its hand-out carried (the fence every outcome report presents, and what the poll
    // reports as held so the lease is renewed). An operator cancel or a revoked lease trips the source, which
    // aborts the in-flight statement. Concurrent because the poll loop registers while executing tasks remove.
    private readonly ConcurrentDictionary<Guid, HeldRunState> _running = new();

    // The compute tasks this node is executing, with the same per-item cancellation discipline.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTasks = new();

    // Released whenever a run or task slot frees, so a poll parked with no free slots is re-issued at once with
    // the new capacity instead of waiting out its wait; one-slot because a single re-poll covers any number of
    // frees.
    private readonly SemaphoreSlim _slotFreed = new(0, 1);

    public RunWorker(
        IServiceProvider services, INodeTransport transport, DocumentExecutor executor, TimeProvider clock,
        ILogger<RunWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _transport = transport;
        _executor = executor;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>This node's identity (its machine name), stamped on every run it is handed so its work is
    /// attributable and recoverable.</summary>
    public string NodeName => _node;

    /// <summary>
    /// Runs the node until <paramref name="stoppingToken"/> is cancelled: poll the dispatcher, execute what it hands
    /// out, report outcomes, repeat. <paramref name="stoppingToken"/> stops this node TAKING work; it does not sever
    /// what the node is already executing. Once it trips, the in-flight runs keep going (and the node keeps polling
    /// with no free slots, so their leases stay alive and the dispatcher never mistakes a draining node for a dead
    /// one) for up to <see cref="RunWorkerOptions.DrainTimeout"/>. Only when that window expires are the survivors
    /// cancelled and left for the dispatcher's lease expiry to requeue.
    /// </summary>
    public async Task RunAsync(RunWorkerOptions options, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentComputeTasks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DrainTimeout, TimeSpan.Zero);

        _startedUtc = _clock.GetUtcNow().UtcDateTime;
        _onRestartRequested = options.OnRestartRequested;
        _pools = options.Pools.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var pollWait = TimeSpan.FromSeconds(Math.Clamp((int)options.PollWait.TotalSeconds, 1, NodeProtocol.MaxWaitSeconds));

        // The abort token for work this node has already been handed. Deliberately NOT linked to stoppingToken: a
        // stop means "stop taking work", and the run keeps executing so it can report its own outcome. This source
        // trips only when the drain window below expires (or at once when the caller granted no window), which is
        // the one case where a run is abandoned mid-flight for the dispatcher to requeue.
        using var abortCts = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(options.MaxConcurrentRuns, options.MaxConcurrentRuns);
        using var computeGate = new SemaphoreSlim(options.MaxConcurrentComputeTasks, options.MaxConcurrentComputeTasks);
        var inFlight = new ConcurrentDictionary<Guid, Task>();
        var computeInFlight = new ConcurrentDictionary<Guid, Task>();
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            var freeRuns = gate.CurrentCount;
            var freeTasks = computeGate.CurrentCount;
            try
            {
                var response = await PollAsync(
                    options, freeRuns, freeTasks, pollWait, allowSlotWake: freeRuns == 0 && freeTasks == 0, stoppingToken)
                    .ConfigureAwait(false);
                backoff = MinBackoff;
                // Applying the answer sits inside the same guard as the poll: nothing that happens to one response
                // (a signal for a run that finished in the meantime, a launch that fails) may ever end the loop,
                // because on the control plane's own node this loop is a hosted service and its fault stops the host.
                await ApplyAsync(response, gate, computeGate, inFlight, computeInFlight, abortCts.Token, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SlotFreedException)
            {
                continue; // a slot freed while a saturated poll was parked: re-poll with the new capacity at once
            }
            catch (Exception ex)
            {
                // The dispatcher is unreachable or not the owner right now (or an answer could not be applied):
                // back off with jitter and try again. Held leases outlive several failed polls, so a brief
                // control-plane blip never loses work.
                LogPollError(SecretHygiene.RedactedMessage(ex), (int)backoff.TotalSeconds);
                if (!await DelayAsync(backoff, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            }
        }

        // Shutdown: no more work is taken; now DRAIN the work this node already holds so each run reports its own
        // outcome instead of being abandoned. The tasks never fault (the execute wrappers catch everything), so
        // this cannot throw, and it keeps both gates alive until every slot is released.
        var pending = inFlight.Values.Concat(computeInFlight.Values).ToArray();
        if (pending.Length > 0)
        {
            await DrainInFlightAsync(pending, options.DrainTimeout, abortCts).ConfigureAwait(false);
        }
    }

    /// <summary>One poll with the node's current capacity and holdings. When <paramref name="allowSlotWake"/> is set
    /// (the node reported no free slots, so the dispatcher cannot hand anything out on this call) a freed slot
    /// cancels the parked poll and surfaces as <see cref="SlotFreedException"/>, so the loop re-polls with the new
    /// capacity at once; a poll that reported free slots is never cancelled, because a hand-out may be in flight.</summary>
    private async Task<NodePollResponse> PollAsync(
        RunWorkerOptions options, int freeRuns, int freeTasks, TimeSpan pollWait, bool allowSlotWake, CancellationToken ct)
    {
        var request = BuildRequest(options, freeRuns, freeTasks, (int)pollWait.TotalSeconds);
        if (!allowSlotWake)
        {
            return await _transport.PollAsync(request, ct).ConfigureAwait(false);
        }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poll = _transport.PollAsync(request, pollCts.Token);
        var wake = _slotFreed.WaitAsync(pollCts.Token);
        var first = await Task.WhenAny(poll, wake).ConfigureAwait(false);
        if (first == poll)
        {
            await pollCts.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(wake).ConfigureAwait(false);
            return await poll.ConfigureAwait(false);
        }

        await pollCts.CancelAsync().ConfigureAwait(false);
        try
        {
            // The poll may still have answered in the same instant (with signals, never with work): honor it.
            return await poll.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SlotFreedException();
        }
    }

    private NodePollRequest BuildRequest(RunWorkerOptions options, int freeRuns, int freeTasks, int waitSeconds)
        => new(
            _node, _version, _pools, options.MaxConcurrentRuns, freeRuns, options.MaxConcurrentComputeTasks, freeTasks,
            _running.Select(r => new HeldRun(r.Key, r.Value.Attempt)).ToList(),
            _runningTasks.Keys.ToList(), _startedUtc, waitSeconds);

    /// <summary>Acts on a poll's answer: trips the cancellation of every run or task the operator asked to cancel and
    /// of every one whose lease was revoked, honors a restart request, and launches the handed-out work.</summary>
    private async Task ApplyAsync(
        NodePollResponse response, SemaphoreSlim gate, SemaphoreSlim computeGate,
        ConcurrentDictionary<Guid, Task> inFlight, ConcurrentDictionary<Guid, Task> computeInFlight,
        CancellationToken abortCt, CancellationToken stoppingToken)
    {
        foreach (var runId in response.CancelRuns)
        {
            if (_running.TryGetValue(runId, out var held))
            {
                LogCancelling(runId);
                await TryCancelAsync(held.Cancellation).ConfigureAwait(false);
            }
        }

        foreach (var runId in response.RevokedRuns)
        {
            if (_running.TryGetValue(runId, out var held))
            {
                LogRevoked(runId, held.Attempt);
                held.Revoked = true;
                await TryCancelAsync(held.Cancellation).ConfigureAwait(false);
            }
        }

        foreach (var taskId in response.CancelTasks)
        {
            if (_runningTasks.TryGetValue(taskId, out var cts))
            {
                LogTaskCancelling(taskId);
                await TryCancelAsync(cts).ConfigureAwait(false);
            }
        }

        foreach (var taskId in response.RevokedTasks)
        {
            if (_runningTasks.TryGetValue(taskId, out var cts))
            {
                LogTaskRevoked(taskId);
                await TryCancelAsync(cts).ConfigureAwait(false);
            }
        }

        if (response.RestartRequested)
        {
            await MaybeHonorRestartAsync(stoppingToken).ConfigureAwait(false);
        }

        foreach (var task in response.Tasks)
        {
            // The dispatcher never hands out more than the free slots the poll reported, and only this loop takes
            // slots, so the wait here is satisfied at once; it is a wait rather than a check so a miscount could
            // never execute over capacity.
            await computeGate.WaitAsync(abortCt).ConfigureAwait(false);
            var execution = ExecuteHandedTaskAsync(task.TaskId, computeGate, abortCt);
            computeInFlight[task.TaskId] = execution;
            _ = execution.ContinueWith(
                _ => computeInFlight.TryRemove(task.TaskId, out Task? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        foreach (var run in response.Runs)
        {
            await gate.WaitAsync(abortCt).ConfigureAwait(false);
            var execution = ExecuteHandedRunAsync(run, gate, abortCt);
            inFlight[run.RunId] = execution;
            _ = execution.ContinueWith(
                _ => inFlight.TryRemove(run.RunId, out Task? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Waits out the work this node already holds after it stopped taking new work, so a routine stop (an autoscaler
    /// reclaiming the replica, a revision swap, an operator restart) costs no run its progress: each in-flight run
    /// finishes and reports its own outcome. The node keeps polling throughout (with no free slots), so its leases
    /// stay alive and the dispatcher leaves the draining work alone rather than requeueing runs that are about to
    /// complete.
    /// <para>Bounded by <paramref name="drainTimeout"/>: a node cannot drain forever, because the platform that asked
    /// it to stop will eventually kill it outright, and a run severed by that kill is strictly worse off than one
    /// cancelled here (the same requeue, minus the log line saying why). When the window expires the survivors are
    /// cancelled through <paramref name="abort"/> and left for the dispatcher's lease expiry. Operator cancels are
    /// still observed while waiting, so a drain can never trap a run an operator has asked to kill.</para>
    /// </summary>
    /// <param name="pending">The in-flight run and compute-task executions to wait on. They never fault (their
    /// execute wrappers catch everything), so this never throws.</param>
    /// <param name="drainTimeout">How long to keep waiting; <see cref="TimeSpan.Zero"/> severs at once.</param>
    /// <param name="abort">The source every in-flight execution runs under, tripped when the window expires.</param>
    /// <remarks>Internal for the test suite; only <see cref="RunAsync"/> calls it in production.</remarks>
    internal async Task DrainInFlightAsync(Task[] pending, TimeSpan drainTimeout, CancellationTokenSource abort)
    {
        var all = Task.WhenAll(pending);
        if (drainTimeout <= TimeSpan.Zero)
        {
            await abort.CancelAsync().ConfigureAwait(false);
            await all.ConfigureAwait(false);
            return;
        }

        LogDraining(pending.Length, (int)drainTimeout.TotalSeconds);
        var deadline = _clock.GetUtcNow() + drainTimeout;
        while (!all.IsCompleted)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                LogDrainTimedOut((int)drainTimeout.TotalSeconds);
                await abort.CancelAsync().ConfigureAwait(false);
                break;
            }

            // CancellationToken.None on the delay: the whole point of the drain is that it outlives the stop signal,
            // so nothing here may abandon the wait early.
            var slice = remaining < DrainPollInterval ? remaining : DrainPollInterval;
            await Task.WhenAny(all, Task.Delay(slice, CancellationToken.None)).ConfigureAwait(false);
            await HeartbeatAsync(abort.Token).ConfigureAwait(false);
        }

        await all.ConfigureAwait(false);
        LogDrained();
    }

    /// <summary>A poll that takes no work: renews the leases of what this node holds and observes cancels and
    /// revocations. Best-effort, never throws (a failed poll is logged; the next one retries), and skipped while the
    /// node holds nothing.</summary>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (_running.IsEmpty && _runningTasks.IsEmpty)
        {
            return;
        }

        try
        {
            var request = new NodePollRequest(
                _node, _version, _pools, 0, 0, 0, 0,
                _running.Select(r => new HeldRun(r.Key, r.Value.Attempt)).ToList(),
                _runningTasks.Keys.ToList(), _startedUtc, 0);
            var response = await _transport.PollAsync(request, ct).ConfigureAwait(false);
            foreach (var runId in response.CancelRuns.Concat(response.RevokedRuns))
            {
                if (_running.TryGetValue(runId, out var held))
                {
                    LogCancelling(runId);
                    await TryCancelAsync(held.Cancellation).ConfigureAwait(false);
                }
            }

            foreach (var taskId in response.CancelTasks.Concat(response.RevokedTasks))
            {
                if (_runningTasks.TryGetValue(taskId, out var cts))
                {
                    LogTaskCancelling(taskId);
                    await TryCancelAsync(cts).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The drain window expired; nothing more to renew.
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex), (int)DrainPollInterval.TotalSeconds);
        }
    }

    /// <summary>Honors an operator's restart request relayed by the dispatcher: logs and invokes the host's
    /// graceful-stop callback exactly once. The callback drives the same shutdown a SIGTERM would, so the loop stops
    /// taking work, drains its in-flight runs and exits, after which the orchestrator recreates the replica. A caller
    /// that supplied no callback simply never restarts.</summary>
    private async Task MaybeHonorRestartAsync(CancellationToken ct)
    {
        if (_onRestartRequested is null || Interlocked.Exchange(ref _restartInitiated, 1) != 0)
        {
            return;
        }

        LogRestartRequested();
        try
        {
            await _onRestartRequested(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The callback drove the shutdown it was meant to; nothing more to do.
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex), 0);
        }
    }

    /// <summary>Trips an in-flight item's cancellation, tolerating the item having finished in the meantime: the
    /// execute wrapper removes the item from its map and then disposes the source, and a poll answer composed a
    /// moment earlier can still name it. A finished item has nothing left to abort, so that is not an error.</summary>
    private static async Task TryCancelAsync(CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The item ended between the lookup and this cancel.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan backoff, CancellationToken ct)
    {
        // Jitter spreads a fleet's retries after a control-plane restart, so hundreds of nodes do not stampede the
        // successor in the same instant.
        var jittered = backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        try
        {
            await Task.Delay(jittered, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The wake wait was cancelled because the poll answered first; nothing to observe.
        }
    }

    private void SignalSlotFreed()
    {
        try
        {
            _slotFreed.Release();
        }
        catch (SemaphoreFullException)
        {
            // A re-poll is already pending; one covers any number of freed slots.
        }
    }

    // ------------------------------------------------------------------------------------------ execution -------

    /// <summary>Executes one handed-out run on its own DI scope and releases the concurrency slot when the run
    /// reaches its end state. Never throws: an abort (the drain window expired) leaves the run for the dispatcher's
    /// lease expiry, and every other failure has already been reported (best-effort) by <see cref="RunHandedAsync"/>,
    /// so one run can never kill the loop or a sibling run.</summary>
    private async Task ExecuteHandedRunAsync(RunHandout handout, SemaphoreSlim gate, CancellationToken abortCt)
    {
        // A per-run source linked to the abort token: an operator cancel or a revoked lease trips only this one
        // (aborting just this run), while an expired drain trips every run through the link. Registered before
        // execution so a cancel arriving the instant after the hand-out is still observed. Disposed only after the
        // run ends, so a late cancel never races a disposed source.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(abortCt);
        var held = new HeldRunState(handout.Attempt, runCts);
        _running[handout.RunId] = held;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await RunHandedAsync(scope.ServiceProvider, catalog, handout, held, abortCt, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortCt.IsCancellationRequested)
        {
            // The drain window expired and severed this run mid-flight: its lease lapses and the dispatcher requeues it.
        }
        catch (Exception ex)
        {
            // RunHandedAsync reports run failures (and operator cancels) itself; this guards the scope plumbing around it.
            LogRunError(handout.RunId, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            _running.TryRemove(handout.RunId, out _);
            gate.Release();
            SignalSlotFreed();
        }
    }

    /// <summary>Executes one handed-out compute task on its own DI scope and releases the slot when it reaches its
    /// end state. Never throws, mirroring <see cref="ExecuteHandedRunAsync"/>.</summary>
    private async Task ExecuteHandedTaskAsync(Guid taskId, SemaphoreSlim gate, CancellationToken abortCt)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(abortCt);
        _runningTasks[taskId] = taskCts;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await RunHandedTaskAsync(scope.ServiceProvider, catalog, taskId, abortCt, taskCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortCt.IsCancellationRequested)
        {
            // The drain window expired and severed the task: its lease lapses and the dispatcher requeues it.
        }
        catch (Exception ex)
        {
            LogTaskError(taskId, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            _runningTasks.TryRemove(taskId, out _);
            gate.Release();
            SignalSlotFreed();
        }
    }

    /// <param name="scope">The task's own DI scope, never shared with another task.</param>
    /// <param name="catalog">The catalog context resolved from <paramref name="scope"/>.</param>
    /// <param name="taskId">The handed-out compute task's id.</param>
    /// <param name="shutdownCt">The abort token: a trip (the drain window expired) leaves the task to the dispatcher.</param>
    /// <param name="taskCt">The per-task token (linked to shutdown): an operator cancel trips this alone, aborting
    /// the in-flight query so the task reports <c>cancelled</c> rather than lapsing.</param>
    private async Task RunHandedTaskAsync(
        IServiceProvider scope, CatalogDbContext catalog, Guid taskId, CancellationToken shutdownCt, CancellationToken taskCt)
    {
        var ct = shutdownCt;
        try
        {
            var row = await catalog.ComputeTasks.AsNoTracking()
                .Where(t => t.TaskId == taskId)
                .Select(t => new { t.Operation, t.SourceRef, t.ArgumentsJson })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                await TryReportTaskAsync(taskId, TaskOutcomeKind.Failed, "the task is no longer in the catalog.", null).ConfigureAwait(false);
                return;
            }

            LogTaskStarting(taskId, row.Operation, row.SourceRef);

            // The queue row is data from the database: parse AND re-validate it here, so a malformed or
            // hand-tampered payload fails the task with a precise message instead of reaching a provider.
            ComputeTaskPayload payload;
            try
            {
                payload = ComputeTaskPayload.FromJson(row.ArgumentsJson);
            }
            catch (SqlFlowException ex)
            {
                await TryReportTaskAsync(taskId, TaskOutcomeKind.Failed, SecretHygiene.RedactedMessage(ex), null).ConfigureAwait(false);
                return;
            }

            var executor = scope.GetRequiredService<ComputeTaskExecutor>();
            // The executor runs under the per-task token so an operator cancel aborts only the in-flight query,
            // while the outcome report below stays on the shutdown token (a late cancel never corrupts it).
            var resultJson = await executor.ExecuteAsync(payload, taskCt).ConfigureAwait(false);

            var recorded = await _transport
                .ReportTaskOutcomeAsync(taskId, new TaskOutcomeRequest(_node, TaskOutcomeKind.Succeeded, null, resultJson), ct)
                .ConfigureAwait(false);
            if (recorded)
            {
                LogTaskSucceeded(taskId, row.Operation);
            }
            else
            {
                LogTaskStale(taskId);
            }
        }
        catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
        {
            // Shutdown: the task is left for the dispatcher's lease expiry to requeue.
            throw;
        }
        catch (Exception) when (taskCt.IsCancellationRequested && !shutdownCt.IsCancellationRequested)
        {
            // The operator cancelled (or the lease was revoked): the per-task token tripped and aborted the in-flight
            // query (surfaced as OperationCanceledException or a provider exception). Report 'cancelled', not 'failed'.
            LogTaskCancelled(taskId);
            await TryReportTaskAsync(taskId, TaskOutcomeKind.Cancelled, null, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed task (unreachable source, bad object, provider error) must never kill the node: report it
            // terminal (best-effort) and continue.
            LogTaskError(taskId, SecretHygiene.RedactedMessage(ex));
            await TryReportTaskAsync(taskId, TaskOutcomeKind.Failed, SecretHygiene.RedactedMessage(ex), null).ConfigureAwait(false);
        }
    }

    private async Task TryReportTaskAsync(Guid taskId, TaskOutcomeKind outcome, string? error, string? resultJson)
    {
        try
        {
            // The original token may be tripped (or the failure a control-plane blip): a short independent deadline
            // still reports the task terminal where the dispatcher is reachable.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _transport
                .ReportTaskOutcomeAsync(taskId, new TaskOutcomeRequest(_node, outcome, error, resultJson), cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex), 0);
        }
    }

    /// <param name="scope">The run's own DI scope, never shared with another run.</param>
    /// <param name="catalog">The catalog context resolved from <paramref name="scope"/>.</param>
    /// <param name="handout">The run's id (the orchestrator-assigned id the trigger returned) and the hand-out's
    /// attempt: the fencing token every outcome report presents, so if the lease lapses and the dispatcher requeues
    /// this run out from under a node presumed dead, that node's late report is dropped instead of clobbering the
    /// successor execution's outcome.</param>
    /// <param name="held">The node's registration of the run, which records whether its lease was revoked.</param>
    /// <param name="shutdownCt">The abort token: it trips only when a stopping node's drain window expired, and the
    /// run is then left for the lease expiry (never reported terminal), so severed work is never lost.</param>
    /// <param name="runCt">The per-run token (linked to shutdown): an operator cancel trips this alone, aborting the
    /// flow's in-flight statement so the run is reported <c>cancelled</c> rather than requeued.</param>
    private async Task RunHandedAsync(
        IServiceProvider scope, CatalogDbContext catalog, RunHandout handout, HeldRunState held,
        CancellationToken shutdownCt, CancellationToken runCt)
    {
        var (runId, attempt) = (handout.RunId, handout.Attempt);
        var ct = shutdownCt;
        try
        {
            // One joined projection instead of three or four sequential lookups: the run row, its repo and
            // pipeline, and the repo's managed source (used only by SHA-pinned runs) arrive in a single round
            // trip. Left joins keep the missing cases distinguishable, so every failure message stays precise.
            var run = await (
                    from r in catalog.Runs.AsNoTracking()
                    where r.RunId == runId
                    join repoRow in catalog.Repos.AsNoTracking() on r.RepoId equals (Guid?)repoRow.Id into repoRows
                    from repo in repoRows.DefaultIfEmpty()
                    join pipelineRow in catalog.Pipelines.AsNoTracking() on r.PipelineId equals pipelineRow.Id into pipelineRows
                    from pipeline in pipelineRows.DefaultIfEmpty()
                    join sourceRow in catalog.RepoSources.AsNoTracking() on repo.Name equals sourceRow.Name into sourceRows
                    from source in sourceRows.DefaultIfEmpty()
                    select new
                    {
                        r.RepoId,
                        r.PipelineId,
                        r.FlowName,
                        r.CommitSha,
                        r.FlowVersionHash,
                        r.FullLoad,
                        r.BackfillFrom,
                        r.BackfillTo,
                        r.FilePattern,
                        r.SourceFilter,
                        r.AssertionsOnly,
                        r.ReprocessFromSourceMin,
                        RepoName = repo != null ? repo.Name : null,
                        RepoRemoteUrl = repo != null ? repo.RemoteUrl : null,
                        RepoRootPath = repo != null ? repo.RootPath : null,
                        PipelineRelativePath = pipeline != null ? pipeline.RelativePath : null,
                        CredentialReference = source != null ? source.CredentialReference : null,
                        CredentialUsername = source != null ? source.CredentialUsername : null,
                    })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is null)
            {
                await FailAsync(runId, attempt, "the run is no longer in the catalog.", ct).ConfigureAwait(false);
                return;
            }

            if (run.RepoId is not { } repoId)
            {
                await FailAsync(runId, attempt, "the run is not attributed to a repository.", ct).ConfigureAwait(false);
                return;
            }

            // Repo.Name and Pipeline.RelativePath are required columns on their tables, so a null projection can
            // only mean the left join found no row: the repo or pipeline has since left the catalog.
            if (run.RepoName is not { } repoName || run.PipelineRelativePath is not { } relativePath)
            {
                await FailAsync(runId, attempt, "the run's repository or pipeline is no longer in the catalog.", ct).ConfigureAwait(false);
                return;
            }

            string flowRoot;
            // Snapshot-first: the enqueue stamped the run with the content hash of the exact YAML to execute and
            // staged that version in the catalog, so the node writes it into its local version cache and runs it
            // with no git access at all. This is what keeps a schedule fanning out a whole batch from storming the
            // git remote with clones (the failure mode: the remote answers a burst of authenticated fetches with
            // throttling, which libgit2 surfaces as "too many redirects or authentication replays"). The git
            // materialization below remains the fallback for a run that carries no snapshot (enqueued before the
            // snapshot model, or its flow embeds a literal credential), a pruned version row, and a document that
            // needs the surrounding repo tree (a relative local source/target/repository path).
            var snapshotRoot = !string.IsNullOrWhiteSpace(run.FlowVersionHash)
                ? await TryStageSnapshotAsync(scope, catalog, runId, run.FlowVersionHash, relativePath, ct).ConfigureAwait(false)
                : null;
            if (snapshotRoot is not null)
            {
                flowRoot = snapshotRoot;
            }
            else if (!string.IsNullOrWhiteSpace(run.CommitSha))
            {
                // SHA-pinned: run the exact committed version, materialized from the repo's remote (reproducible,
                // and works even on a node with no locally synced copy of this flow).
                if (string.IsNullOrWhiteSpace(run.RepoRemoteUrl))
                {
                    await FailAsync(runId, attempt,
                        $"run is pinned to commit '{run.CommitSha}' but repository '{repoName}' has no remote URL to materialize from.", ct).ConfigureAwait(false);
                    return;
                }

                // The git credential comes from the repo's registered source (matched by name, projected above),
                // whose stored ${...} reference points at the vault/env holding the token; a repo synced by the
                // CLI with no source falls back to the host environment. The node fetches the secret itself: it is
                // the mobile execution engine that resolves what it needs, and only the reference travelled
                // through the catalog.
                var resolver = scope.GetRequiredService<ISecretResolver>();
                var credentials = await GitMaterializer
                    .ResolveCredentialsAsync(resolver, run.CredentialReference, run.CredentialUsername, ct)
                    .ConfigureAwait(false);
                flowRoot = _materializer.Materialize(run.RepoRemoteUrl, run.CommitSha, credentials, ct);
                LogMaterialized(runId, repoName, run.CommitSha);
            }
            else
            {
                // Unpinned: run from the node's locally synced repo path (the default).
                if (string.IsNullOrWhiteSpace(run.RepoRootPath))
                {
                    await FailAsync(runId, attempt, $"repository '{repoName}' has no synced root path on this node.", ct).ConfigureAwait(false);
                    return;
                }

                flowRoot = run.RepoRootPath;
            }

            var flowFile = Path.GetFullPath(Path.Combine(flowRoot, relativePath));
            if (!File.Exists(flowFile))
            {
                await FailAsync(runId, attempt, $"the flow file for '{run.FlowName}' was not found on this node.", ct).ConfigureAwait(false);
                return;
            }

            LogStarting(runId, run.FlowName, repoName);

            var documents = scope.GetRequiredService<YamlDocumentLoader>();
            var document = DocumentLoader.Load(documents, flowFile, message => LogHygiene(runId, message));

            // The handed-out run id is the orchestrator-assigned id: the engine stamps it on the run and its
            // artifact, so the run records under exactly the id the trigger returned. Run-log echo stays null
            // (server side). The run's substitution parameters (the built-in backfill) travel from the queue row
            // into the engine here: the one handoff point, shared by every flow kind.
            var parameters = new RunParameters
            {
                FullLoad = run.FullLoad,
                BackfillFrom = run.BackfillFrom,
                BackfillTo = run.BackfillTo,
                FilePattern = run.FilePattern,
                SourceFilter = run.SourceFilter,
                AssertionsOnly = run.AssertionsOnly,
                ReprocessFromSourceMin = run.ReprocessFromSourceMin,
            };
            if (!parameters.IsDefault)
            {
                LogParameters(runId, parameters.Describe());
            }

            // Downstream-anchored watermark (the default for every incremental flow): the flow reads its
            // high-water MAX from the next durable table in the lineage chain (the ods/silver table it feeds), not
            // its own target, so deleting rows there re-opens the window and the source is re-pulled. Bronze is
            // driven by what silver holds. The downstream table is resolved here, where the catalog's lineage graph
            // is available; the engine tier has no catalog, so this is the single point that can compute it. A null
            // result (no lineage, an ambiguous chain, or a direct CLI run) leaves the probe on the flow's own
            // target. Both flow kinds that carry an incremental watermark participate: file flows (FlowRunner) and
            // relational ingestion flows (IngestionFlowRunner).
            var (incrementalWatermark, ownSchema, ownName) = document switch
            {
                IngestionFlowDocument ing when ing.Document.Flow.Incremental.IsIncremental
                    => (true, ing.Document.Flow.Target.Table.Schema, ing.Document.Flow.Target.Table.Name),
                FileFlowDocument fileDoc when fileDoc.Flow.Incremental is { FullLoad: false }
                    => (true, fileDoc.Flow.Target.Schema, fileDoc.Flow.Target.Table),
                _ => (false, string.Empty, string.Empty),
            };

            RelationalObject? watermarkSourceTable = null;
            if (incrementalWatermark)
            {
                watermarkSourceTable = await ResolveDownstreamWatermarkTableAsync(
                    catalog, repoId, run.PipelineId, ownSchema, ownName, ct).ConfigureAwait(false);
                if (watermarkSourceTable is not null)
                {
                    LogDownstreamWatermark(runId, watermarkSourceTable.QualifiedName);
                }
            }

            // Landing-reset verdict (load.resetWhenConsolidated, on by default): a chained landing (bronze)
            // table is pure staging, so once every flow that directly reads its typed view has completed a
            // successful run after this flow's last successful load, the engine may truncate it before this
            // run's load. One hop only, by design: delivery to the NEXT phase (silver) frees the landing table;
            // whether anything further downstream ran is irrelevant. Resolved here because the verdict needs the
            // catalog's lineage graph and run ledger, which the engine tier does not have. Null (no consumers in
            // the lineage, or a non-participating flow) leaves the landing table alone without comment.
            LandingReset? landingReset = null;
            if (document is FileFlowDocument landingDoc
                && landingDoc.Flow.Load is { Mode: LoadMode.Append, ResetWhenConsolidated: true }
                && landingDoc.Flow.Inference.GeneratesView)
            {
                landingReset = await ResolveLandingResetAsync(
                    catalog, repoId, run.PipelineId,
                    landingDoc.Flow.Target.Schema, landingDoc.Flow.Target.Table, parameters, ct).ConfigureAwait(false);
                if (landingReset is not null)
                {
                    LogLandingReset(runId, landingReset.Authorized ? "authorized" : "blocked", landingReset.Reason);
                }
            }

            // The node streams the run's generated SQL and its canonical events into the catalog live: as each
            // statement executes a CatalogRunStatement row is written, and as each event is published (a file
            // read, a resolved watermark, a stage summary) a CatalogRunEvent row is written, each on its sink's
            // own scope/context, so the Statements and Events views update while the run is still running and
            // both streams survive even a mid-run crash. The sinks are disposed at the end of this block
            // (draining every queued write) BEFORE the outcome report below, whose projection keeps these live
            // rows and appends only the tail they missed: the artifact stays authoritative.
            DocumentExecutionResult exec;
            await using (var statementSink = new CatalogRunStatementSink(_services, runId, repoId, _logger))
            await using (var eventSink = new CatalogRunEventSink(_services, runId, repoId, _logger))
            {
                var options = new DocumentExecutionOptions
                {
                    RunId = runId, Echo = null, Parameters = parameters, StatementSink = statementSink,
                    EventSink = eventSink, WatermarkSourceTable = watermarkSourceTable, LandingReset = landingReset,
                    // The handed-out run's flow name selects WHICH flow of the document executes: for an ingestion
                    // document with an embedded healthCheck: block, the derived hc pipeline runs from the same file.
                    FlowName = run.FlowName,
                };
                // The executor runs under the per-run token: an operator cancel aborts the in-flight statement here
                // (and only here), while the surrounding bookkeeping stays on the shutdown token so a late cancel
                // never corrupts the outcome report.
                exec = await _executor.ExecuteAsync(document, flowFile, options, runCt).ConfigureAwait(false);
            }

            if (exec.RunDirectory is { } runDirectory)
            {
                var runJson = Path.Combine(runDirectory, "run.json");
                var artifact = await ReadArtifactAsync(runJson, ct).ConfigureAwait(false);
                var status = artifact.Json is not null
                    ? await _transport
                        .ReportRunOutcomeAsync(runId, new RunOutcomeRequest(_node, attempt, RunOutcomeKind.Completed, null, artifact.Json), ct)
                        .ConfigureAwait(false)
                    : await _transport
                        .ReportRunOutcomeAsync(runId, new RunOutcomeRequest(_node, attempt, RunOutcomeKind.Failed,
                            $"the run executed but its result could not be recorded: {artifact.Error}.", null), ct)
                        .ConfigureAwait(false);
                if (status == RunOutcomeStatus.StaleClaim)
                {
                    // The lease lapsed and the run was requeued (and possibly handed out again) out from under this
                    // node while it executed: the fence dropped this report, the successor execution's outcome is
                    // authoritative, and the flow's idempotent load makes the double execution harmless. Loud in
                    // the log because it means this node's polls went unanswered for the whole lease.
                    LogStaleClaim(runId, attempt);
                }
            }
            else
            {
                // No artifact was written (an IO failure while writing the run history): report the outcome
                // directly so the run still reaches a terminal state.
                await FailAsync(runId, attempt, SecretHygiene.RedactedMessage(exec.Error ?? "the run produced no artifact."), ct).ConfigureAwait(false);
            }

            if (exec.Success)
            {
                LogSucceeded(runId, run.FlowName, exec.DurationSeconds);
            }
            else
            {
                LogFailed(runId, run.FlowName, SecretHygiene.RedactedMessage(exec.Error ?? "(no error message)"));
            }
        }
        catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
        {
            // Shutdown cancelled this run mid-flight: leave it for the dispatcher's lease expiry to requeue.
            throw;
        }
        catch (Exception) when (runCt.IsCancellationRequested && !shutdownCt.IsCancellationRequested)
        {
            if (held.Revoked)
            {
                // The dispatcher revoked the lease (it lapsed and the run was requeued): there is nothing to report,
                // because the fence would drop it; the successor execution is authoritative.
                LogRevokedAborted(runId, attempt);
                return;
            }

            // The operator cancelled this run: the per-run token tripped and aborted the in-flight statement (which
            // SqlClient may surface as OperationCanceledException or a SqlException), so its transaction rolled back.
            // Report it 'cancelled' - not 'failed' - and continue.
            LogCancelled(runId);
            await TryReportRunAsync(runId, attempt, RunOutcomeKind.Cancelled, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed run (bad flow, unreachable database, IO) must never kill the node: report it terminal
            // (best-effort) and continue.
            LogRunError(runId, SecretHygiene.RedactedMessage(ex));
            await TryReportRunAsync(runId, attempt, RunOutcomeKind.Failed, SecretHygiene.RedactedMessage(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the run's artifact for the outcome report, refusing one over the protocol's size bound with a
    /// precise reason rather than shipping it.</summary>
    private static async Task<(string? Json, string? Error)> ReadArtifactAsync(string runJsonPath, CancellationToken ct)
    {
        try
        {
            var length = new FileInfo(runJsonPath).Length;
            if (length > NodeProtocol.MaxArtifactBytes)
            {
                return (null, $"the run artifact is {length} bytes, over the {NodeProtocol.MaxArtifactBytes}-byte limit");
            }

            return (await File.ReadAllTextAsync(runJsonPath, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, SecretHygiene.RedactedMessage(ex));
        }
    }

    // The staged-YAML version cache: one immutable directory per content hash, under the same per-user node cache
    // the git materializer writes its checkouts to. The "yaml" namespace can never collide with a repo directory
    // (those are keyed by a 16-char remote-URL hash). Stability matters: runs of the same version share the
    // directory, so the run history written next to the flow file accumulates across runs exactly as it does in a
    // per-commit git checkout.
    private static readonly string SnapshotCacheRoot =
        Path.Combine(Path.GetTempPath(), "sqlflow", "node-cache", "yaml");

    /// <summary>
    /// Stages the run's snapshotted YAML version from the catalog into the local version cache and returns the
    /// directory to execute from (the flow file lands at its repo-relative path beneath it, so the run-history
    /// anchor matches a git checkout's layout). Returns null, sending the caller to the git materialization path,
    /// when the version row no longer exists, the document cannot execute from a bare snapshot (it references
    /// sibling repo files through a relative local path), the stored text does not parse (the git path reproduces
    /// the same load error the run would have reported before snapshots), or the cache directory cannot be
    /// written (logged; the git path is the still-correct degradation).
    /// </summary>
    private async Task<string?> TryStageSnapshotAsync(
        IServiceProvider scope, CatalogDbContext catalog, Guid runId, string contentHash, string relativePath,
        CancellationToken ct)
    {
        var yaml = await catalog.FlowVersions.AsNoTracking()
            .Where(v => v.ContentHash == contentHash)
            .Select(v => v.Yaml)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null; // the version row is gone; the commit pin still identifies the exact content in git
        }

        var versionRoot = Path.Combine(SnapshotCacheRoot, contentHash);
        var flowFile = Path.GetFullPath(Path.Combine(versionRoot, relativePath));

        // One probing parse decides executability from a bare snapshot; the run itself still loads through the
        // same DocumentLoader.Load path as every other execution mode (CLI file, git checkout).
        try
        {
            var documents = scope.GetRequiredService<YamlDocumentLoader>();
            if (RequiresRepoTree(documents.Parse(yaml, flowFile)))
            {
                return null;
            }
        }
        catch (SqlFlowException)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(flowFile)!);
            if (!File.Exists(flowFile))
            {
                // Content-addressed, so concurrent runs of the same version race benignly (identical bytes):
                // publish with a private temp write + move, so a torn write is never observed and a move that
                // loses the race just means a sibling run staged it first.
                var temp = flowFile + ".staging-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(temp, yaml, ct).ConfigureAwait(false);
                    File.Move(temp, flowFile);
                }
                catch (IOException) when (File.Exists(flowFile))
                {
                    // A concurrent run published this version between the existence check and the move.
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }

            LogSnapshotStaged(runId, contentHash);
            return versionRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The local cache is unwritable (disk pressure, permissions): degrade to the git path, which needs no
            // pre-staged file, and say why so the slower clone is explainable from the log.
            LogSnapshotStageFailed(runId, contentHash, SecretHygiene.RedactedMessage(ex));
            return null;
        }
    }

    /// <summary>True when the document cannot execute from a bare single-file snapshot because it addresses
    /// sibling files in the repo tree: a relative local source location (a file flow reading committed sample
    /// data), a relative export target path, or a relative source-control working directory. These resolve
    /// against the flow file's own directory, which only a full git materialization populates. Cloud URIs
    /// (<c>scheme://</c>) and absolute local paths resolve identically under either root and stay snapshot-safe.
    /// Internal for the test suite; only the staging path above calls it in production.</summary>
    internal static bool RequiresRepoTree(FlowDocument document)
        => document switch
        {
            FileFlowDocument doc => IsLocalRelative(doc.Flow.Source.Location),
            ExportFlowDocument doc => IsLocalRelative(doc.Document.Flow.TrgPath),
            SourceControlFlowDocument doc => IsLocalRelative(doc.Document.Flow.Repository.WorkingDirectory),
            _ => false,
        };

    private static bool IsLocalRelative(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !path.Contains("://", StringComparison.Ordinal)
           && !Path.IsPathRooted(path);

    /// <summary>
    /// Resolves the next durable table downstream of this flow in the lineage chain, for downstream-anchored
    /// watermarking (incremental.watermarkFromDownstream). It walks the persisted lineage the sync already
    /// computed: the flow-level dependencies name the flows that consume this one (they read an object it
    /// writes/creates), and each of those flows' Writes/Creates edges name the durable tables they populate. The
    /// flow's own target is excluded (a downstream flow writing back to it is not a "next" table). Anchoring is
    /// applied only when exactly one such table resolves with a full three-part identity: an ambiguous chain (a
    /// fan-out to several tables) or a partially-identified object is left to fall back to the flow's own target,
    /// so the watermark is never anchored to a guessed table. Returns null when there is no unambiguous next table.
    /// </summary>
    private static async Task<RelationalObject?> ResolveDownstreamWatermarkTableAsync(
        CatalogDbContext catalog, Guid repoId, Guid pipelineId, string ownSchema, string ownName, CancellationToken ct)
    {
        var downstreamPipelineIds = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId && d.FromPipelineId == pipelineId)
            .Select(d => d.ToPipelineId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (downstreamPipelineIds.Count == 0)
        {
            return null;
        }

        var writeKeys = await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId == repoId
                && e.PipelineId != null && downstreamPipelineIds.Contains(e.PipelineId.Value)
                && (e.Relation == "Writes" || e.Relation == "Creates"))
            .Select(e => e.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (writeKeys.Count == 0)
        {
            return null;
        }

        // Resolve the write targets to fully-identified table objects (a table, with a database and schema, so the
        // engine can introspect and probe it). Views and partially-resolved objects are dropped here.
        var candidates = await catalog.Objects.AsNoTracking()
            .Where(o => writeKeys.Contains(o.Key)
                && o.Kind == "Table"
                && o.Database != null && o.Schema != null)
            .Select(o => new { o.Database, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Exclude the flow's own target (a downstream flow writing back to it is not a "next" table). Matched on
        // schema + name only: a file flow's target carries no database part, and a table's schema-qualified name
        // is unique enough within a repo's estate to identify "this is my own target".
        var distinct = candidates
            .Where(c => !(string.Equals(c.Schema, ownSchema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Name, ownName, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c => (
                c.Database!.ToLowerInvariant(),
                c.Schema!.ToLowerInvariant(),
                c.Name.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        if (distinct.Count != 1)
        {
            return null;
        }

        var only = distinct[0];
        return new RelationalObject { Database = only.Database!, Schema = only.Schema!, Name = only.Name };
    }

    /// <summary>
    /// Resolves the landing-reset verdict for a chained landing (file) flow: may the engine truncate the flow's
    /// landing target before this run's load? The verdict is one hop only, by design: the landing (bronze) table
    /// is freed when the NEXT phase (silver) has its data, so the gate is that every flow DIRECTLY consuming this
    /// flow (per the persisted lineage dependencies) has completed a successful run that started after this
    /// flow's last successful run ended, proving everything the last load staged was visible to and consumed by
    /// every reader. What any later phase (gold) holds never enters the verdict. Two additional conditions keep
    /// the truncate honest: every consumer must read the engine's typed view <c>[schema].[v_&lt;table&gt;]</c>
    /// (the chained landing contract; a consumer reading the base table, or through a hand-written object, may
    /// depend on rows accumulating, so the reset is refused rather than guessed), and this flow must have a
    /// prior successful run to anchor the comparison (a seeded table with no run history is never truncated on
    /// faith). A run bounded to a window or filter (an operator backfill of a slice) is refused outright: the
    /// whole staged dataset must reach the downstream merge, so a partial re-land never truncates first; a plain
    /// forced full load keeps the normal gate and becomes a clean staging rebuild when authorized. Returns null
    /// when the flow has no lineage consumers at all: a plain append flow that is nobody's staging area is left
    /// alone without comment. Every blocked verdict carries the reason, so a landing table that keeps growing
    /// has a loud, queryable explanation on each run.
    /// </summary>
    internal static async Task<LandingReset?> ResolveLandingResetAsync(
        CatalogDbContext catalog, Guid repoId, Guid pipelineId, string targetSchema, string targetTable,
        RunParameters parameters, CancellationToken ct)
    {
        var consumers = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId && d.FromPipelineId == pipelineId)
            .Select(d => new { d.ToPipelineId, d.ToFlow })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (consumers.Count == 0)
        {
            return null;
        }

        // A window- or filter-bounded run (an operator backfilling a specific slice) deliberately re-lands only
        // part of the source, and the whole staged dataset must reach the downstream merge: resetting first
        // would leave the landing table holding just the slice, which a downstream full-refresh consumer could
        // then rebuild from. Never reset a bounded run. A plain forced full load (--full, no window or filter)
        // keeps the normal gate below: it re-lands everything the definition selects, so an authorized reset
        // turns it into a clean staging rebuild instead of doubling the table.
        if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null
            || !string.IsNullOrWhiteSpace(parameters.FilePattern) || !string.IsNullOrWhiteSpace(parameters.SourceFilter))
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = "this run is bounded to a window/filter (backfill); a partial re-land never resets the landing table",
            };
        }

        // The chained landing contract: consumers read the engine-generated typed view over the landing table.
        // Resolve which consumers actually read [targetSchema].[v_<targetTable>] from their persisted Reads
        // edges; any consumer that depends on this flow through some other object gets the rows preserved.
        var viewName = $"v_{targetTable}";
        var consumerIds = consumers.Select(c => c.ToPipelineId).ToList();
        var reads = await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId == repoId
                && e.PipelineId != null && consumerIds.Contains(e.PipelineId.Value)
                && e.Relation == "Reads")
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectKey })
            .ToListAsync(ct).ConfigureAwait(false);
        var readKeys = reads.Select(r => r.ObjectKey).Distinct().ToList();
        var viewKeys = await catalog.Objects.AsNoTracking()
            .Where(o => readKeys.Contains(o.Key)
                && o.Kind == "View"
                && o.Schema == targetSchema && o.Name == viewName)
            .Select(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);
        var viewKeySet = viewKeys.ToHashSet(StringComparer.Ordinal);
        var viewReaders = reads.Where(r => viewKeySet.Contains(r.ObjectKey)).Select(r => r.PipelineId).ToHashSet();
        var nonViewConsumer = consumers.FirstOrDefault(c => !viewReaders.Contains(c.ToPipelineId));
        if (nonViewConsumer is not null)
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = $"consumer '{nonViewConsumer.ToFlow}' does not read the typed view [{targetSchema}].[{viewName}], "
                    + "so its contract with this table is unknown and the staged rows are preserved",
            };
        }

        // The consolidation anchor: this flow's last successful load. No prior success means nothing this flow
        // loaded is proven delivered (a seeded or hand-filled table has no ledger entry to compare against).
        var lastLoadEnd = await catalog.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipelineId && r.Status == RunStatuses.Succeeded && r.EndUtc != null)
            .MaxAsync(r => (DateTime?)r.EndUtc, ct).ConfigureAwait(false);
        if (lastLoadEnd is null)
        {
            return new LandingReset
            {
                Authorized = false,
                Reason = "this flow has no prior successful run, so nothing staged is proven consolidated",
            };
        }

        // Delivery proof, per consumer: a successful run that STARTED after the last load ENDED saw every staged
        // row (a run that started earlier may have read a partial table, so it proves nothing). A consumer that
        // is disabled or failing blocks the reset indefinitely, loudly: growth is the correct failure mode when
        // delivery cannot be proven, silent data loss is not.
        foreach (var consumer in consumers)
        {
            var consumed = await catalog.Runs.AsNoTracking()
                .AnyAsync(r => r.PipelineId == consumer.ToPipelineId
                    && r.Status == RunStatuses.Succeeded
                    && r.StartUtc != null && r.StartUtc >= lastLoadEnd, ct).ConfigureAwait(false);
            if (!consumed)
            {
                return new LandingReset
                {
                    Authorized = false,
                    Reason = $"consumer '{consumer.ToFlow}' has no successful run after this flow's last load at {lastLoadEnd:u}",
                };
            }
        }

        return new LandingReset
        {
            Authorized = true,
            Reason = $"every consumer ({string.Join(", ", consumers.Select(c => c.ToFlow))}) completed a successful run "
                + $"after this flow's last load at {lastLoadEnd:u}",
        };
    }

    // Every outcome report below presents the hand-out's fence (this node's name + the attempt): if the lease lapsed
    // and the dispatcher requeued the run in the meantime (this node was presumed dead), the report is dropped and the
    // successor execution's outcome stands - a stale report must lose to the fence, never race it.
    private async Task FailAsync(Guid runId, int attempt, string error, CancellationToken ct)
    {
        var status = await _transport
            .ReportRunOutcomeAsync(runId, new RunOutcomeRequest(_node, attempt, RunOutcomeKind.Failed, error, null), ct)
            .ConfigureAwait(false);
        if (status == RunOutcomeStatus.StaleClaim)
        {
            LogStaleClaim(runId, attempt);
        }
    }

    private async Task TryReportRunAsync(Guid runId, int attempt, RunOutcomeKind outcome, string? error)
    {
        try
        {
            // The original cancellation token may be tripped (or the failure may have been a transport blip): use a
            // short independent deadline so the run is still reported terminal where the dispatcher is reachable.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var status = await _transport
                .ReportRunOutcomeAsync(runId, new RunOutcomeRequest(_node, attempt, outcome, error, null), cts.Token)
                .ConfigureAwait(false);
            if (status == RunOutcomeStatus.StaleClaim)
            {
                LogStaleClaim(runId, attempt);
            }
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex), 0);
        }
    }

    public void Dispose() => _slotFreed.Dispose();

    /// <summary>A run this node holds: the attempt its hand-out carried, its cancellation, and whether the dispatcher
    /// revoked its lease (so an abort is not reported as an operator cancel).</summary>
    private sealed class HeldRunState(int attempt, CancellationTokenSource cancellation)
    {
        public int Attempt { get; } = attempt;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool Revoked { get; set; }
    }

    /// <summary>Raised inside the poll loop when a slot freed while a saturated poll was parked, so the loop re-polls
    /// with the new capacity at once. Never escapes <see cref="RunAsync"/>.</summary>
    private sealed class SlotFreedException : Exception
    {
        public SlotFreedException()
            : base("a slot freed while the poll was parked.")
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: materialized repo '{Repo}' at commit {CommitSha}.")]
    private partial void LogMaterialized(Guid runId, string repo, string commitSha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: staged flow version {ContentHash} from the catalog snapshot (no git access needed).")]
    private partial void LogSnapshotStaged(Guid runId, string contentHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: could not stage flow version {ContentHash} into the local cache ({Error}); falling back to git materialization.")]
    private partial void LogSnapshotStageFailed(Guid runId, string contentHash, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: substitution parameters applied: {Parameters}.")]
    private partial void LogParameters(Guid runId, string parameters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: watermark anchored to downstream table {Table} (incremental.watermarkFromDownstream).")]
    private partial void LogDownstreamWatermark(Guid runId, string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: landing reset {Verdict} (load.resetWhenConsolidated): {Reason}.")]
    private partial void LogLandingReset(Guid runId, string verdict, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} starting: flow '{FlowName}' in repo '{Repo}'.")]
    private partial void LogStarting(Guid runId, string flowName, string repo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} succeeded: flow '{FlowName}' in {DurationSeconds}s.")]
    private partial void LogSucceeded(Guid runId, string flowName, double durationSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} failed: flow '{FlowName}': {Error}")]
    private partial void LogFailed(Guid runId, string flowName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: operator cancel observed; aborting the in-flight statement.")]
    private partial void LogCancelling(Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: lease at attempt {Attempt} revoked by the dispatcher (it lapsed and the run was requeued); aborting this execution.")]
    private partial void LogRevoked(Guid runId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: execution at attempt {Attempt} aborted after its lease was revoked; no outcome reported, the successor execution is authoritative.")]
    private partial void LogRevokedAborted(Guid runId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} cancelled by operator; reported cancelled.")]
    private partial void LogCancelled(Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} threw and was reported failed: {Error}")]
    private partial void LogRunError(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispatcher poll error: {Error} (retrying in about {BackoffSeconds}s)")]
    private partial void LogPollError(string error, int backoffSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping: no longer taking work; draining {Count} in-flight item(s) so each reports its own outcome (up to {DrainSeconds}s).")]
    private partial void LogDraining(int count, int drainSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drained: every in-flight item finished and reported its outcome.")]
    private partial void LogDrained();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drain window of {DrainSeconds}s expired with work still in flight; cancelling it. Each severed run's lease lapses and the dispatcher requeues it, which consumes one of its execution attempts. Raise the drain window, or the platform's termination grace period, if this recurs.")]
    private partial void LogDrainTimedOut(int drainSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: outcome report dropped by the fence (attempt {Attempt}): the run was requeued out from under this node while it executed, so the successor execution's outcome is authoritative. This node's polls went unanswered for the whole lease; check for control-plane connectivity gaps or a paused container.")]
    private partial void LogStaleClaim(Guid runId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart requested by an operator; draining in-flight work and exiting so the orchestrator recreates this node.")]
    private partial void LogRestartRequested();

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} starting: {Operation} against {SourceRef}.")]
    private partial void LogTaskStarting(Guid taskId, string operation, string sourceRef);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} succeeded ({Operation}).")]
    private partial void LogTaskSucceeded(Guid taskId, string operation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compute task {TaskId}: outcome report dropped; the task no longer belonged to this node.")]
    private partial void LogTaskStale(Guid taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId}: operator cancel observed; aborting the in-flight query.")]
    private partial void LogTaskCancelling(Guid taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compute task {TaskId}: lease revoked by the dispatcher; aborting this execution.")]
    private partial void LogTaskRevoked(Guid taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} cancelled by operator; reported cancelled.")]
    private partial void LogTaskCancelled(Guid taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Compute task {TaskId} threw and was reported failed: {Error}")]
    private partial void LogTaskError(Guid taskId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {Message}")]
    private partial void LogHygiene(Guid runId, string message);
}
