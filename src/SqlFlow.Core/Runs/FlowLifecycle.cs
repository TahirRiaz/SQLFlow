namespace SqlFlow.Core.Runs;

/// <summary>
/// Which stage of its life a flow document declares itself in (the YAML <c>lifecycle:</c>, valid on every
/// document kind). Today the lifecycle gates exactly one behavior: only <see cref="Production"/> pipelines
/// generate notification events (a failing flow under active development must never page its subscribers).
/// Execution itself is unaffected: a development flow runs, schedules, and records history exactly like a
/// production one, so promotion is a one-line YAML change with no behavioral surprises.
/// </summary>
public enum FlowLifecycle
{
    /// <summary>A live pipeline (the default when the document declares nothing): failures alert subscribers.</summary>
    Production = 0,

    /// <summary>A pipeline under active development: it runs normally but never generates notification events.</summary>
    Development = 1,
}
