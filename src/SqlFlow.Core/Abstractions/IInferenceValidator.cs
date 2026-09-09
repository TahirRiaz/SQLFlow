using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Runs the inferred conversions against the live table to sanity-check them: for each converted
/// column it counts the non-null values and how many of those the conversion would turn into NULL
/// (silent data loss). Server-side and set-based, so it scales to large tables.
/// </summary>
public interface IInferenceValidator
{
    Task<IReadOnlyList<ConversionCheckResult>> CheckAsync(
        string connectionString,
        string schema,
        string table,
        IReadOnlyList<ConversionCheck> checks,
        ServerLocale locale,
        CancellationToken ct = default);
}
