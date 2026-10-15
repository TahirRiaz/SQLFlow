using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// One step of the control plane's start-up pass, which a host module adds after the built-in ones: applies what the
/// deployment's configuration says the module needs (its partitions, its settings, its registrations), at every start.
/// A step is repeatable (applying the same configuration again changes nothing) and throws to say it failed, with a
/// message an operator can act on; the pass retries a failed step, and readiness names it until it succeeds.
/// </summary>
public interface IBootstrapStep
{
    /// <summary>The step's name as readiness and <c>GET /api/v1/bootstrap</c> report it: lowercase words joined by '-', unique in the host.</summary>
    string Name { get; }

    /// <summary>
    /// Applies the step with <paramref name="services"/>, a scope of the host's services opened for it, and answers what it
    /// did in a few words (<c>registered dev, test</c>, <c>unchanged</c>). Never a secret.
    /// </summary>
    Task<string> ApplyAsync(IServiceProvider services, CancellationToken ct);
}

/// <summary>Where one step of the start-up pass stands.</summary>
public static class BootstrapStepStates
{
    /// <summary>Not reached yet.</summary>
    public const string Pending = "pending";

    /// <summary>Being applied now.</summary>
    public const string Running = "running";

    /// <summary>Applied in this process's pass.</summary>
    public const string Done = "done";

    /// <summary>Failed, and retried after a pause.</summary>
    public const string Failed = "failed";

    /// <summary>Refused for a mistake retrying cannot fix; the pass stopped there.</summary>
    public const string Refused = "refused";
}

/// <summary>
/// One step as the start-up pass last left it: its state (<see cref="BootstrapStepStates"/>), what it did, the error it last
/// failed with (redacted), how many attempts it took, and when it last changed.
/// </summary>
public sealed record BootstrapStepState(string Name, string State, string? Summary, string? Error, int Attempts, DateTime? ChangedUtc);

/// <summary>
/// The start-up pass's progress, step by step: what readiness reports (<see cref="BootstrapReadiness"/>) and what
/// <c>GET /api/v1/bootstrap</c> answers, so whoever deployed the control plane sees which step it is on, which failed and
/// why, without reading its log.
/// </summary>
public sealed class BootstrapProgress
{
    private readonly Lock _gate = new();
    private readonly List<BootstrapStepState> _steps = [];

    /// <summary>Every step of the pass in order, as each last stood.</summary>
    public IReadOnlyList<BootstrapStepState> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>True once every step is done.</summary>
    public bool Completed
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count > 0 && _steps.All(s => s.State == BootstrapStepStates.Done);
            }
        }
    }

    /// <summary>Lays out the pass's steps, all pending.</summary>
    public void Plan(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_gate)
        {
            _steps.Clear();
            _steps.AddRange(names.Select(name => new BootstrapStepState(name, BootstrapStepStates.Pending, null, null, 0, null)));
        }
    }

    internal void Started(string name, DateTime nowUtc)
        => Update(name, s => s with { State = BootstrapStepStates.Running, Attempts = s.Attempts + 1, ChangedUtc = nowUtc });

    internal void Succeeded(string name, string summary, DateTime nowUtc)
        => Update(name, s => s with { State = BootstrapStepStates.Done, Summary = summary, Error = null, ChangedUtc = nowUtc });

    internal void Failed(string name, string error, DateTime nowUtc)
        => Update(name, s => s with { State = BootstrapStepStates.Failed, Error = error, ChangedUtc = nowUtc });

    internal void Refused(string name, string error, DateTime nowUtc)
        => Update(name, s => s with { State = BootstrapStepStates.Refused, Error = error, ChangedUtc = nowUtc });

    private void Update(string name, Func<BootstrapStepState, BootstrapStepState> change)
    {
        lock (_gate)
        {
            var at = _steps.FindIndex(s => s.Name == name);
            if (at < 0)
            {
                throw new InvalidOperationException($"The start-up pass has no step '{name}'.");
            }

            _steps[at] = change(_steps[at]);
        }
    }
}

/// <summary>
/// Whether the start-up pass has completed, as a health check served at <c>/health/startup</c> (tagged <see cref="Tag"/>):
/// unhealthy until every step is done, naming the step it is on or the one that failed. It is not part of readiness: a
/// control plane with a failed step still serves the API, among it <c>GET /api/v1/bootstrap</c>, which answers the reason
/// to a caller with read access (the health endpoint answers anyone, so it names the step only).
/// </summary>
public sealed class BootstrapReadiness : IHealthCheck
{
    /// <summary>The tag of the start-up check: served at <c>/health/startup</c>, and left out of <c>/health/ready</c>.</summary>
    public const string Tag = "startup";

    private readonly BootstrapProgress _progress;

    public BootstrapReadiness(BootstrapProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progress = progress;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var steps = _progress.Steps;
        var waiting = steps.FirstOrDefault(s => s.State != BootstrapStepStates.Done);
        var result = steps.Count == 0
            ? HealthCheckResult.Unhealthy("The start-up pass has not begun.")
            : waiting is null
                ? HealthCheckResult.Healthy($"All {steps.Count} start-up steps are done.")
                : HealthCheckResult.Unhealthy(waiting.State switch
                {
                    BootstrapStepStates.Failed => $"Start-up step '{waiting.Name}' failed and is retried.",
                    BootstrapStepStates.Refused => $"Start-up step '{waiting.Name}' was refused; the control plane needs its configuration put right.",
                    _ => $"Waiting for start-up step '{waiting.Name}'.",
                });
        return Task.FromResult(result);
    }
}
