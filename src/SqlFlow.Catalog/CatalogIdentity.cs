using SqlFlow.Core.Identity;

namespace SqlFlow.Catalog;

/// <summary>
/// The catalog's stable identities. A pipeline's identity is scoped to its repo: the SAME flow name in two
/// different repos is two different pipelines (the catalog spans repos), so the id mixes the repo in. It stays
/// deterministic (no database round-trip), so a run discovered under a repo's estate computes the same pipeline
/// id the registry sync did, and the two join.
/// </summary>
public static class CatalogIdentity
{
    /// <summary>The deterministic, repo-scoped pipeline id for a flow name within a repo.</summary>
    public static Guid Pipeline(Guid repoId, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return FlowIdentity.FromName($"{repoId:N}/{flowName}");
    }

    /// <summary>
    /// The deterministic id of a YAML-declared schedule: one per NAME within a repo, so re-syncing the same estate
    /// updates the schedule row in place rather than duplicating it, and every flow that joins the name lands on the
    /// one row. Names are matched case-insensitively (as the estate scan resolves them), so the id is computed from
    /// the lowercased name and two spellings of a name cannot become two schedules. API-created schedules get a
    /// fresh id instead.
    /// </summary>
    public static Guid YamlSchedule(Guid repoId, string scheduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        return FlowIdentity.FromName($"{repoId:N}/schedule/{scheduleName.ToLowerInvariant()}");
    }
}
