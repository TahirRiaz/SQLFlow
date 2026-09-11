using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Hosts the shared <see cref="RunWorker"/> loop as a control-plane background service: the very same node runtime
/// a standalone <c>sqlflow worker</c> runs, here polling the dispatcher in-process (no HTTP, no serialization)
/// through <see cref="InProcessNodeTransport"/>. It serves the pools configured under <c>ControlPlane:Worker:Pools</c>
/// (empty = untargeted runs only, the single-node default) and executes up to <c>Worker:MaxConcurrentRuns</c> at once.
/// </summary>
public sealed class RunExecutionWorker : BackgroundService
{
    private readonly RunWorker _worker;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RunWorkerOptions _options;

    public RunExecutionWorker(RunWorker worker, IHostApplicationLifetime lifetime, IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(options);
        _worker = worker;
        _lifetime = lifetime;
        var worker0 = options.Value.Worker;
        _options = new RunWorkerOptions
        {
            Pools = worker0.Pools,
            MaxConcurrentRuns = worker0.MaxConcurrentRuns,
            MaxConcurrentComputeTasks = worker0.MaxConcurrentComputeTasks,
            PollWait = TimeSpan.FromSeconds(options.Value.Dispatch.LongPollSeconds),
            DrainTimeout = RunWorker.DefaultDrainTimeout,
            // An operator restart of this in-process node stops the whole control-plane host (which, on the
            // single-node default that runs a worker in-process, is exactly the process hosting the node): the
            // framework then drains and the container is recreated. In the scaled estate the compute nodes are
            // separate worker processes with Worker:Enabled=false here, so this path never stops an API-only replica.
            OnRestartRequested = _ =>
            {
                _lifetime.StopApplication();
                return Task.CompletedTask;
            },
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _worker.RunAsync(_options, stoppingToken);
}
