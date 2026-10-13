using SqlFlow.Dispatch.ManagedWorkers;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// Managed workers (section <c>ControlPlane:ManagedWorkers</c>): the control plane starts its own workers through a
/// launcher a host module registers, keeps every managed pool at least at its minimum, starts more for queued runs no
/// free slot covers, and tells idle surplus workers to leave once they hold nothing. Off by default; with it off nothing
/// about dispatch changes. The default pool is always managed (<see cref="MinWorkers"/>, <see cref="MaxWorkers"/>); a
/// named pool is managed when it is listed under <see cref="Pools"/>.
/// </summary>
public sealed class ManagedWorkersOptions
{
    /// <summary>The configuration section, for messages.</summary>
    public const string Section = "ControlPlane:ManagedWorkers";

    /// <summary>Turns managed workers on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The name of a launcher a host module registered. Required when enabled.</summary>
    public string? Launcher { get; set; }

    /// <summary>Workers the default pool always keeps; at least 1.</summary>
    public int MinWorkers { get; set; } = 1;

    /// <summary>Most workers the default pool runs for demand.</summary>
    public int MaxWorkers { get; set; } = 10;

    /// <summary>The named pools to manage, by pool name.</summary>
    public Dictionary<string, ManagedPoolOptions> Pools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How often the control plane compares each pool with what it needs (1 to 60).</summary>
    public int TickSeconds { get; set; } = 3;

    /// <summary>How long a surplus worker stays idle before it is asked to leave.</summary>
    public int IdleMinutes { get; set; } = 5;

    /// <summary>How long a started worker may take to poll (and a worker may stay silent) before it is stopped.</summary>
    public int StartTimeoutMinutes { get; set; } = 10;

    /// <summary>How long a worker told to leave may keep polling before it is stopped.</summary>
    public int ExitTimeoutMinutes { get; set; } = 5;

    /// <summary>The age at which a worker is replaced; it finishes its work first.</summary>
    public int MaxLifetimeHours { get; set; } = 12;

    /// <summary>Whether workers this control plane process did not start are replaced, so a redeploy moves every worker
    /// onto the new build.</summary>
    public bool ReplaceWorkersOnStart { get; set; } = true;

    /// <summary>The most workers started in one tick, across every pool.</summary>
    public int MaxLaunchesPerTick { get; set; } = 20;

    /// <summary>The prefix of every managed worker's name.</summary>
    public string NamePrefix { get; set; } = WorkerNames.DefaultPrefix;

    /// <summary>Rejects settings that cannot work while the feature is on, naming the setting; does nothing while it is
    /// off, so a default never stops a host that does not use it.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Launcher))
        {
            throw new InvalidOperationException(
                $"{Section}:Launcher is required when {Section}:Enabled is true: name the launcher, registered by a host module, that starts the workers.");
        }

        foreach (var name in Pools.Keys)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
            {
                throw new InvalidOperationException(
                    $"{Section}:Pools has a pool named '{name}'; a pool name is not blank and has no surrounding spaces (the default pool is set by {Section}:MinWorkers and MaxWorkers).");
            }
        }

        // Upper bounds first, so a value too large to be a duration is refused by name rather than overflowing.
        foreach (var (setting, value, max) in new[]
        {
            (nameof(IdleMinutes), IdleMinutes, 10080),
            (nameof(StartTimeoutMinutes), StartTimeoutMinutes, 1440),
            (nameof(ExitTimeoutMinutes), ExitTimeoutMinutes, 1440),
            (nameof(MaxLifetimeHours), MaxLifetimeHours, 8760),
        })
        {
            if (value > max)
            {
                throw new InvalidOperationException($"{Section}:{setting} must be at most {max}; it is {value}.");
            }
        }

        ToSettings(defaultRunSlots: 1, WorkerNames.UnknownBuild, TimeSpan.FromSeconds(45)).Validate(Section);
    }

    /// <summary>The settings the managed fleet runs with. <paramref name="defaultRunSlots"/> is what a worker executes at
    /// once before any worker of its pool has reported it, <paramref name="buildTag"/> this control plane's build
    /// (<see cref="SqlFlow.ControlPlane.Dispatch.ControlPlaneBuild"/>), and <paramref name="pollFreshness"/> how recently a
    /// worker must have polled to count as present (the dispatcher's long poll plus a margin).</summary>
    public ManagedFleetSettings ToSettings(int defaultRunSlots, string buildTag, TimeSpan pollFreshness)
        => new()
        {
            Pools =
            [
                new ManagedPoolSettings(string.Empty, MinWorkers, MaxWorkers),
                .. Pools.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new ManagedPoolSettings(p.Key, p.Value.MinWorkers, p.Value.MaxWorkers)),
            ],
            Tick = TimeSpan.FromSeconds(TickSeconds),
            Idle = TimeSpan.FromMinutes(IdleMinutes),
            StartTimeout = TimeSpan.FromMinutes(StartTimeoutMinutes),
            ExitTimeout = TimeSpan.FromMinutes(ExitTimeoutMinutes),
            MaxLifetime = TimeSpan.FromHours(MaxLifetimeHours),
            ReplaceWorkersOnStart = ReplaceWorkersOnStart,
            MaxLaunchesPerTick = MaxLaunchesPerTick,
            NamePrefix = NamePrefix,
            DefaultRunSlots = defaultRunSlots,
            BuildTag = buildTag,
            PollFreshness = pollFreshness,
        };
}

/// <summary>One named pool's managed workers (section <c>ControlPlane:ManagedWorkers:Pools:&lt;name&gt;</c>).</summary>
public sealed class ManagedPoolOptions
{
    /// <summary>Workers the pool always keeps; at least 1.</summary>
    public int MinWorkers { get; set; } = 1;

    /// <summary>Most workers the pool runs for demand.</summary>
    public int MaxWorkers { get; set; } = 10;
}
