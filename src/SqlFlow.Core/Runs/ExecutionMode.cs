namespace SqlFlow.Core.Runs;

/// <summary>
/// When a definition executes: <see cref="Auto"/> runs it as part of every automatic execution (a scheduled
/// fire, a batch/node group member, an ingestion run's assertion step), <see cref="Manual"/> reserves it for an
/// explicit on-demand trigger (the GUI's run button, the API's single-flow trigger, a direct CLI run), and
/// <see cref="Disabled"/> marks it deactivated: a retired source or run-once replay that automatic execution
/// must never pick up, excluded exactly like a manual one but carrying the retirement visibly. Both non-auto
/// modes stay directly runnable, since a deactivated pipeline is still replayed by hand when needed. Applies to
/// every flow document (the envelope <c>mode:</c> key) and to each ingestion assertion individually.
/// </summary>
public enum ExecutionMode
{
    /// <summary>Executes automatically (the default and the pre-existing behavior).</summary>
    Auto = 0,

    /// <summary>Executes only when explicitly triggered on demand.</summary>
    Manual = 1,

    /// <summary>Deactivated: never part of automatic execution, still triggerable directly by hand.</summary>
    Disabled = 2,
}
