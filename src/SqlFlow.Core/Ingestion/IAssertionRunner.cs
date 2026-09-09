namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Runs the flow's data-quality assertions against the loaded target. Like the run log, this is a two-mode
/// seam: WITHOUT-database mode uses <see cref="NullAssertionRunner"/> (no registry, runs nothing); WITH-database
/// mode injects a SQL-backed runner over an <see cref="IAssertionDefinitionStore"/>. Log-only by default: an
/// assertion outcome never fails or rolls back the load.
/// </summary>
public interface IAssertionRunner
{
    /// <param name="flow">The flow whose assertion definitions are evaluated.</param>
    /// <param name="targetConnectionString">Connection to the loaded target the assertions query.</param>
    /// <param name="includeManual">False (every automatic ingestion run) evaluates only the assertions declared
    /// <c>mode: auto</c>; true (an on-demand assertions-only run) evaluates the flow's whole list, auto and
    /// manual alike.</param>
    /// <param name="ct">Cancels the assertion evaluation.</param>
    Task<IReadOnlyList<AssertionResult>> RunAsync(
        IngestionFlow flow, string targetConnectionString, bool includeManual = false, CancellationToken ct = default);
}

/// <summary>The without-database default: evaluates no assertions.</summary>
public sealed class NullAssertionRunner : IAssertionRunner
{
    public static readonly NullAssertionRunner Instance = new();

    public Task<IReadOnlyList<AssertionResult>> RunAsync(
        IngestionFlow flow, string targetConnectionString, bool includeManual = false, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AssertionResult>>([]);
}
