using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Hosts the shared <see cref="RunWorker"/> drain loop as a control-plane background service. The loop itself (claim
/// a queued run, execute it through the shared engine, record the outcome) is the node runtime that a standalone
/// <c>sqlflow worker</c> also runs; here it idles on the in-process <see cref="RunQueueSignal"/> so a triggered run
/// starts within milliseconds, with the runtime's poll interval as the fallback for schedule- and other-node-enqueued
/// work. It serves the pools configured under <c>ControlPlane:Worker:Pools</c> (empty = untargeted runs only, the
/// single-node default).
/// </summary>
public sealed class RunExecutionWorker : BackgroundService
{
    private readonly RunWorker _worker;
    private readonly RunQueueSignal _signal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IReadOnlyList<string> _pools;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxConcurrentRuns;

    public RunExecutionWorker(
        RunWorker worker, RunQueueSignal signal, IHostApplicationLifetime lifetime, IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(options);
        _worker = worker;
        _signal = signal;
        _lifetime = lifetime;
        _pools = options.Value.Worker.Pools;
        // Both bounds come from ControlPlane:Worker and are validated at startup (ControlPlaneOptions.Validate):
        // the poll fallback (a nudge starts a triggered run at once; this only governs schedule-/other-node-enqueued
        // runs and recovery) has a 250ms floor so a bad value cannot spin-loop, and the run concurrency has a floor
        // of one (a strictly serial node).
        _pollInterval = TimeSpan.FromMilliseconds(options.Value.Worker.PollMilliseconds);
        _maxConcurrentRuns = options.Value.Worker.MaxConcurrentRuns;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        // An operator restart of this in-process node stops the whole control-plane host (which, on the single-node
        // default that runs a worker in-process, is exactly the process hosting the node): the framework then drains
        // and the container is recreated. In the scaled estate the compute nodes are separate worker processes with
        // Worker:Enabled=false here, so this path never stops an API-only replica.
        => _worker.RunAsync(
            _pollInterval, _pools, (timeout, ct) => _signal.WaitAsync(timeout, ct), stoppingToken, _maxConcurrentRuns,
            onRestartRequested: _ =>
            {
                _lifetime.StopApplication();
                return Task.CompletedTask;
            });
}
