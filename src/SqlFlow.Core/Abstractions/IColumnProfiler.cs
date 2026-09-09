using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Profiles raw (string) columns against the target engine's own conversion semantics. Running the
/// probe server-side (set-based <c>TRY_CONVERT</c>) keeps data in place and guarantees that any type
/// the inferencer then picks is genuinely convertible by SQL Server.
/// </summary>
public interface IColumnProfiler
{
    Task<IReadOnlyList<ColumnProfile>> ProfileAsync(
        string connectionString,
        string schema,
        string table,
        IReadOnlyList<string> columns,
        TypeInferencePolicy policy,
        ServerLocale locale,
        CancellationToken ct = default);
}
