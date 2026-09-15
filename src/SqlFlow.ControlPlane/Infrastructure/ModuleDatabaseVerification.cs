using Microsoft.Extensions.Diagnostics.HealthChecks;
using SqlFlow.Catalog.Modules;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// Whether bootstrap has verified every module database against the build, as a readiness check: healthy with no module
/// databases, unhealthy until bootstrap has verified them, and unhealthy with the refusal when one was behind, ahead or
/// missing (the control plane is then stopping).
/// </summary>
public sealed class ModuleDatabaseVerification : IHealthCheck
{
    private readonly int _databases;
    private volatile string? _refusal;
    private volatile bool _verified;

    public ModuleDatabaseVerification(IEnumerable<ModuleDatabase> databases)
    {
        ArgumentNullException.ThrowIfNull(databases);
        _databases = databases.Count();
    }

    /// <summary>True once every module database has been verified current.</summary>
    public bool Verified => _verified;

    /// <summary>The refusal, when a module database was not current.</summary>
    public string? Refusal => _refusal;

    internal void MarkVerified() => _verified = true;

    internal void MarkRefused(string refusal) => _refusal = refusal;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = _databases == 0
            ? HealthCheckResult.Healthy("No module databases are registered.")
            : _refusal is { } refusal
                ? HealthCheckResult.Unhealthy(refusal)
                : _verified
                    ? HealthCheckResult.Healthy($"{_databases} module database(s) are current.")
                    : HealthCheckResult.Unhealthy($"{_databases} module database(s) are not verified yet.");
        return Task.FromResult(result);
    }
}
