namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The sink for the per-run execution record. This is the seam that keeps the engine working in BOTH modes:
/// in WITHOUT-database (YAML) mode the runner uses <see cref="NullIngestionRunLog"/> and a run never touches a
/// control table; in WITH-database (full) mode a SQL-backed implementation persists the record to
/// flw.SysLog / flw.SysStats on its own configured control connection. The implementation owns its connection,
/// so the runner does not need a separate control-DB connection to log.
/// </summary>
public interface IIngestionRunLog
{
    Task WriteAsync(IngestionRunRecord record, CancellationToken ct = default);
}

/// <summary>The without-database default: records nothing. A lightweight YAML run creates no control tables.</summary>
public sealed class NullIngestionRunLog : IIngestionRunLog
{
    public static readonly NullIngestionRunLog Instance = new();

    public Task WriteAsync(IngestionRunRecord record, CancellationToken ct = default) => Task.CompletedTask;
}
