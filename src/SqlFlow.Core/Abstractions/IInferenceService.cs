using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// The dedicated inference capability: reads a loaded table and determines the optimal SQL type per
/// column, returning a JSON-serializable <see cref="InferenceReport"/>. Independent of the pipeline
/// engine - it can be invoked on its own (its own spec) or hooked into a load.
/// </summary>
public interface IInferenceService
{
    /// <summary>Profiles the table and returns the inferred types plus the runnable transform SELECT.</summary>
    Task<InferenceReport> InferAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Infers, then runs the conversions against the table to sanity-check them: the returned report's
    /// <see cref="InferenceReport.Validation"/> is populated with a per-column fit status and flags any
    /// column whose non-null values would silently become NULL.
    /// </summary>
    Task<InferenceReport> ValidateAsync(InferenceRequest request, CancellationToken ct = default);
}
