namespace SqlFlow.Dispatch.ManagedWorkers;

/// <summary>One managed pool: how many workers it always keeps, and the most it runs for demand. <see cref="Pool"/> is
/// the pool key (the empty string is the default pool).</summary>
public sealed record ManagedPoolSettings(string Pool, int MinWorkers, int MaxWorkers);

/// <summary>
/// What the managed fleet runs with. A host binds its own configuration into this and calls <see cref="Validate"/>
/// before starting the fleet, so a setting that cannot work stops the host with the setting's name rather than
/// misbehaving later.
/// </summary>
public sealed class ManagedFleetSettings
{
    /// <summary>The most workers any pool may be given, matching the ceiling the fleet controls accept.</summary>
    public const int MaxWorkersCeiling = 100;

    /// <summary>The managed pools. The default pool (the empty key) is always one of them.</summary>
    public required IReadOnlyList<ManagedPoolSettings> Pools { get; init; }

    /// <summary>How often the fleet compares each pool with what it needs.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a surplus worker stays idle before it is asked to leave.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a started worker may take to poll, and how long a worker may stay silent, before it is stopped.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a worker told to leave may keep polling before it is stopped.</summary>
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The age at which a worker is replaced.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Whether workers this control plane process did not start are replaced, so a redeploy moves every worker
    /// onto the new build.</summary>
    public bool ReplaceWorkersOnStart { get; init; } = true;

    /// <summary>The most workers started in one tick, across every pool.</summary>
    public int MaxLaunchesPerTick { get; init; } = 20;

    /// <summary>The prefix of every managed worker's name.</summary>
    public string NamePrefix { get; init; } = WorkerNames.DefaultPrefix;

    /// <summary>How many runs a worker executes at once when no worker of its pool has reported its own capacity yet.</summary>
    public int DefaultRunSlots { get; init; } = 4;

    /// <summary>The first wait after a failed start; it doubles with every further failure, up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait between starts of a pool whose starts keep failing.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many fleet actions are kept for the Nodes page.</summary>
    public int RecentActions { get; init; } = 200;

    /// <summary>The longest any one launcher call (and the read of the pools' floors) may take before the tick gives up
    /// on it. Also the tick's budget for starting workers: what it has not started by then waits for the next tick.</summary>
    public TimeSpan LauncherCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How recently a Ready worker must have polled to count as present when the fleet decides whether a pool
    /// can spare a worker: a healthy worker polls at least once per long poll, so the dispatcher's long poll plus a margin.</summary>
    public TimeSpan PollFreshness { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a new worker must keep polling before its pool's failed starts are forgiven; a worker that ends
    /// sooner, unasked, counts as a failed start. Long enough that a worker which crashes some minutes into its life (a
    /// slow crash loop) keeps its pool backed off.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long after its last poll a worker that went silent still counts toward its pool's minimum (never as
    /// capacity), so a blip shorter than this (a network hiccup, a restarting proxy) starts no replacement.</summary>
    public TimeSpan SilentGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>This control plane build's tag, four lowercase hex digits written into every worker name it starts, so a
    /// later control plane tells the workers of its own build (kept across a failover) from another build's (replaced
    /// after a redeploy, with <see cref="ReplaceWorkersOnStart"/>).</summary>
    public string BuildTag { get; init; } = WorkerNames.UnknownBuild;

    /// <summary>Rejects settings that cannot work, naming the setting under <paramref name="section"/>.</summary>
    /// <exception cref="InvalidOperationException">A setting is out of range, or two pools would give their workers the
    /// same name.</exception>
    public void Validate(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (Pools is null || !Pools.Any(p => p.Pool.Length == 0))
        {
            throw new InvalidOperationException($"{section}: the default pool is always managed.");
        }

        foreach (var pool in Pools)
        {
            var where = pool.Pool.Length == 0 ? section : $"{section}:Pools:{pool.Pool}";
            if (pool.MinWorkers < 1)
            {
                throw new InvalidOperationException(
                    $"{where}:MinWorkers must be at least 1: a managed pool always keeps a worker running.");
            }

            if (pool.MaxWorkers < pool.MinWorkers || pool.MaxWorkers > MaxWorkersCeiling)
            {
                throw new InvalidOperationException(
                    $"{where}:MaxWorkers must be between MinWorkers ({pool.MinWorkers}) and {MaxWorkersCeiling}.");
            }
        }

        var duplicate = Pools.GroupBy(p => p.Pool, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"{section}:Pools lists '{duplicate.Key}' more than once.");
        }

        WorkerNames.ValidatePrefix(NamePrefix, $"{section}:NamePrefix");
        WorkerNames.ValidatePools(Pools.Select(p => p.Pool), $"{section}:Pools");

        if (Tick < TimeSpan.FromSeconds(1) || Tick > TimeSpan.FromSeconds(60))
        {
            throw new InvalidOperationException($"{section}:TickSeconds must be between 1 and 60.");
        }

        if (Idle < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException($"{section}:IdleMinutes must be at least 1.");
        }

        if (StartTimeout < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException($"{section}:StartTimeoutMinutes must be at least 1.");
        }

        if (ExitTimeout < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException($"{section}:ExitTimeoutMinutes must be at least 1.");
        }

        if (MaxLifetime < TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException($"{section}:MaxLifetimeHours must be at least 1.");
        }

        if (MaxLaunchesPerTick is < 1 or > MaxWorkersCeiling)
        {
            throw new InvalidOperationException($"{section}:MaxLaunchesPerTick must be between 1 and {MaxWorkersCeiling}.");
        }

        if (DefaultRunSlots < 1 || RecentActions < 1 || MinBackoff <= TimeSpan.Zero || MaxBackoff < MinBackoff
            || LauncherCallTimeout <= TimeSpan.Zero || PollFreshness <= TimeSpan.Zero || StableAfter <= TimeSpan.Zero
            || SilentGrace < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{section}: the run slot default, the action history, the back-off, the launcher call timeout, the poll freshness and the stability window must be positive, and the silence grace not negative.");
        }

        WorkerNames.ValidateBuildTag(BuildTag, $"{section} build tag");
    }

    /// <summary>The settings of one pool, or null when the pool is not managed.</summary>
    public ManagedPoolSettings? PoolOf(string poolKey)
        => Pools.FirstOrDefault(p => string.Equals(p.Pool, poolKey, StringComparison.Ordinal));
}
