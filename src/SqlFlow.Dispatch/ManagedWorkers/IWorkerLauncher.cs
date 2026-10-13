namespace SqlFlow.Dispatch.ManagedWorkers;

/// <summary>
/// Starts and stops compute nodes on a platform, for the managed fleet (<see cref="ManagedFleet"/>). A host module
/// registers one implementation per platform it supports; configuration selects one by <see cref="Name"/>. The fleet
/// decides what to start and when, and tells idle workers to leave through the dispatcher, so a launcher only has to
/// start a worker, say which workers it has, and stop one that cannot leave by itself.
/// </summary>
/// <remarks>
/// A worker the launcher starts must poll the control plane under the name in <see cref="WorkerLaunch.Name"/> (the
/// node's <c>--node-name</c>), serve exactly the pool in <see cref="WorkerLaunch.Pool"/> (none for the default pool), and
/// exit when a poll answer tells it to. Credentials reach the worker from the platform's own secret store or, for a
/// worker on the control plane's own machine, through its process environment; never through a launch request a
/// platform would store.
/// </remarks>
public interface IWorkerLauncher
{
    /// <summary>The name configuration selects this launcher by: lowercase, for example <c>jobs</c>.</summary>
    string Name { get; }

    /// <summary>The longest the platform lets one worker run before it ends it (a job's time limit, a credential's
    /// lifetime), or null when nothing does. The fleet drains a worker well before this, whatever its pool's minimum, so
    /// it finishes its runs on its own terms rather than being cut off.</summary>
    TimeSpan? WorkerTimeLimit => null;

    /// <summary>The build of the deployment the control plane and its workers both come from, when the platform names one
    /// (the commit a platform built and deployed every component from), or null when it does not. When given, the fleet
    /// tells a redeploy from a restart by it, so a change to the worker alone still replaces the workers; otherwise by
    /// the control plane's own assemblies.</summary>
    string? DeploymentBuild => null;

    /// <summary>The workers this launcher started that the platform still knows about, including those that ended
    /// recently (<see cref="LaunchedWorkerState.Ended"/>), so a worker that died before its first poll is seen as a
    /// failed start. Throws <see cref="WorkerLauncherException"/> when the platform cannot be asked.</summary>
    Task<IReadOnlyList<LaunchedWorker>> ListAsync(CancellationToken ct);

    /// <summary>Starts one worker. Returns once the platform accepted it; the worker polls some time later. Throws
    /// <see cref="WorkerLauncherException"/> when the platform refused it or could not be reached.</summary>
    Task LaunchAsync(WorkerLaunch launch, CancellationToken ct);

    /// <summary>Ends a worker at once: one that never started, went silent, or ignored the request to leave. A worker
    /// the platform no longer knows is not an error. Throws <see cref="WorkerLauncherException"/> when the platform
    /// cannot be asked.</summary>
    Task StopAsync(string workerName, CancellationToken ct);
}

/// <summary>One worker to start: the name it polls under, and the pool it serves (the empty string is the default
/// pool, whose workers take untargeted runs only).</summary>
public sealed record WorkerLaunch(string Name, string Pool);

/// <summary>Where a launched worker is, as the platform reports it.</summary>
public enum LaunchedWorkerState
{
    /// <summary>Accepted, but not running yet (scheduled, pulling its image, starting).</summary>
    Starting,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Finished, failed or stopped; it no longer runs.</summary>
    Ended,
}

/// <summary>A worker a launcher started, as the platform reports it now. <see cref="LaunchedUtc"/> is when the
/// platform created it, when it says; <see cref="Detail"/> is the platform's own word on its state (a reason it
/// failed, an exit code), for the record of what happened.</summary>
public sealed record LaunchedWorker(string Name, LaunchedWorkerState State, DateTime? LaunchedUtc, string? Detail = null);

/// <summary>A launcher could not do what it was asked. The message is safe to show and to log: an implementation
/// redacts and bounds whatever the platform answered before putting it here. <see cref="MayHaveStarted"/> says a
/// launch failed in a way that leaves it unknown whether the worker was created (a timeout after the request was
/// sent), so the fleet keeps counting it as starting rather than starting another one at once.</summary>
public sealed class WorkerLauncherException : Exception
{
    public WorkerLauncherException()
    {
    }

    public WorkerLauncherException(string message)
        : base(message)
    {
    }

    public WorkerLauncherException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerLauncherException(string message, bool mayHaveStarted, Exception? innerException = null)
        : base(message, innerException)
    {
        MayHaveStarted = mayHaveStarted;
    }

    /// <summary>Whether a failed launch may still have created the worker.</summary>
    public bool MayHaveStarted { get; }
}
