namespace SqlFlow.Core.Runs;

/// <summary>The bounds every reader of a run artifact enforces, shared so a writer can stay inside them.</summary>
public static class RunArtifactLimits
{
    /// <summary>The largest <c>run.json</c> the catalog records. A larger artifact is refused with a warning rather
    /// than parsed, so a hostile or corrupt file cannot exhaust memory or bloat the catalog's text columns.</summary>
    public const long MaxRunJsonBytes = 64L * 1024 * 1024;
}
