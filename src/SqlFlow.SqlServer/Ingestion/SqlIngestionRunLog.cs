using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// The WITH-database run log: persists each run to flw.SysLog (one live row per flow, PK on FlowID, matching
/// legacy) and archives the prior live row to flw.SysStats before overwriting it, so history is preserved. It
/// owns its control connection string (a control database, or the target). The tables are created idempotently
/// on first use (V3 has no registration trigger), counts are BIGINT (the legacy INT columns truncated past
/// 2.1B rows), and the live-row write is an explicit UPDATE then conditional INSERT (no MERGE, consistent with
/// the rest of the engine). All metric values are bound as parameters, never interpolated.
/// </summary>
public sealed class SqlIngestionRunLog : IIngestionRunLog
{
    private const int ObjectAlreadyExists = 2714;
    private const int ProcessNameMax = 500;

    private readonly string _connectionString;

    public SqlIngestionRunLog(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task WriteAsync(IngestionRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
        await WriteRowAsync(connection, record, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            await using var command = new SqlCommand(EnsureSchemaSql, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == ObjectAlreadyExists)
        {
            // Concurrent first-use race between the IF-NOT-EXISTS check and CREATE; another run won, which is fine.
        }
    }

    private static async Task WriteRowAsync(SqlConnection connection, IngestionRunRecord record, CancellationToken ct)
    {
        // Archive the existing live row (if any) to SysStats, then overwrite the live row in place. The archive
        // runs first so it captures the PRIOR run; the IF @@ROWCOUNT = 0 guard handles the first run for a flow.
        await using var command = new SqlCommand(ArchiveThenUpsertSql, connection) { CommandTimeout = 0 };
        var process = record.Process.Length > ProcessNameMax ? record.Process[..ProcessNameMax] : record.Process;

        command.Parameters.Add(new SqlParameter("@FlowID", SqlDbType.Int) { Value = record.FlowId });
        command.Parameters.Add(new SqlParameter("@RunId", SqlDbType.UniqueIdentifier) { Value = record.RunId });
        AddString(command, "@FlowType", record.FlowType);
        AddString(command, "@Process", process);
        AddString(command, "@Batch", record.Batch);
        AddString(command, "@SysAlias", record.SysAlias);
        AddString(command, "@ExecMode", record.ExecMode);
        command.Parameters.Add(new SqlParameter("@StartTime", SqlDbType.DateTime2) { Value = record.StartTimeUtc });
        command.Parameters.Add(new SqlParameter("@EndTime", SqlDbType.DateTime2) { Value = record.EndTimeUtc });
        command.Parameters.Add(new SqlParameter("@DurationFlow", SqlDbType.Int) { Value = record.DurationSeconds });
        command.Parameters.Add(new SqlParameter("@Fetched", SqlDbType.BigInt) { Value = record.RowsFetched });
        command.Parameters.Add(new SqlParameter("@Inserted", SqlDbType.BigInt) { Value = record.RowsInserted });
        command.Parameters.Add(new SqlParameter("@Updated", SqlDbType.BigInt) { Value = record.RowsUpdated });
        command.Parameters.Add(new SqlParameter("@Deleted", SqlDbType.BigInt) { Value = record.RowsDeleted });
        command.Parameters.Add(new SqlParameter("@Success", SqlDbType.Bit) { Value = record.Success });
        command.Parameters.Add(new SqlParameter("@FlowRate", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = record.FlowRate });
        command.Parameters.Add(new SqlParameter("@NoOfThreads", SqlDbType.Int) { Value = (object?)record.Threads ?? DBNull.Value });
        AddString(command, "@SelectCmd", record.SelectCmd);
        AddString(command, "@InsertCmd", record.InsertCmd);
        AddString(command, "@UpdateCmd", record.UpdateCmd);
        AddString(command, "@CreateCmd", record.CreateCmd);
        AddString(command, "@TraceLog", record.TraceLog);
        AddString(command, "@ErrorRuntime", record.Error);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddString(SqlCommand command, string name, string? value)
        => command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar) { Value = (object?)value ?? DBNull.Value });

    private const string EnsureSchemaSql = """
        IF SCHEMA_ID(N'flw') IS NULL EXEC(N'CREATE SCHEMA [flw]');
        IF OBJECT_ID(N'[flw].[SysLog]', N'U') IS NULL
        CREATE TABLE [flw].[SysLog] (
            [FlowID] int NOT NULL,
            [RunId] uniqueidentifier NULL,
            [FlowType] varchar(25) NULL,
            [Process] nvarchar(500) NULL,
            [Batch] varchar(250) NULL,
            [SysAlias] varchar(70) NULL,
            [ExecMode] varchar(50) NULL,
            [StartTime] datetime2(3) NULL,
            [EndTime] datetime2(3) NULL,
            [DurationFlow] int NULL,
            [DurationPre] int NULL,
            [DurationPost] int NULL,
            [Fetched] bigint NULL,
            [Inserted] bigint NULL,
            [Updated] bigint NULL,
            [Deleted] bigint NULL,
            [Success] bit NULL,
            [FlowRate] decimal(18,2) NULL,
            [NoOfThreads] int NULL,
            [SelectCmd] nvarchar(max) NULL,
            [InsertCmd] nvarchar(max) NULL,
            [UpdateCmd] nvarchar(max) NULL,
            [CreateCmd] nvarchar(max) NULL,
            [ErrorRuntime] nvarchar(max) NULL,
            [TraceLog] nvarchar(max) NULL,
            CONSTRAINT [PK_flw_SysLog] PRIMARY KEY CLUSTERED ([FlowID]));
        IF OBJECT_ID(N'[flw].[SysStats]', N'U') IS NULL
        CREATE TABLE [flw].[SysStats] (
            [SysStatsID] bigint IDENTITY(1,1) NOT NULL,
            [FlowType] varchar(25) NULL,
            [StatsDate] datetime2(3) NULL,
            [FlowID] int NULL,
            [StartTime] datetime2(3) NULL,
            [EndTime] datetime2(3) NULL,
            [DurationFlow] int NULL,
            [DurationPre] int NULL,
            [DurationPost] int NULL,
            [Fetched] bigint NULL,
            [Inserted] bigint NULL,
            [Updated] bigint NULL,
            [Deleted] bigint NULL,
            [Success] bit NULL,
            [FlowRate] decimal(18,2) NULL,
            [NoOfThreads] int NULL,
            [ExecMode] varchar(50) NULL,
            CONSTRAINT [PK_flw_SysStats] PRIMARY KEY CLUSTERED ([SysStatsID]));
        """;

    private const string ArchiveThenUpsertSql = """
        INSERT INTO [flw].[SysStats] ([FlowType],[StatsDate],[FlowID],[StartTime],[EndTime],[DurationFlow],[DurationPre],[DurationPost],[Fetched],[Inserted],[Updated],[Deleted],[Success],[FlowRate],[NoOfThreads],[ExecMode])
        SELECT TOP (1) [FlowType], SYSUTCDATETIME(), [FlowID],[StartTime],[EndTime],[DurationFlow],[DurationPre],[DurationPost],[Fetched],[Inserted],[Updated],[Deleted],[Success],[FlowRate],[NoOfThreads],[ExecMode]
        FROM [flw].[SysLog] WHERE [FlowID] = @FlowID;

        UPDATE [flw].[SysLog]
        SET [RunId]=@RunId, [FlowType]=@FlowType, [Process]=@Process, [Batch]=@Batch, [SysAlias]=@SysAlias, [ExecMode]=@ExecMode,
            [StartTime]=@StartTime, [EndTime]=@EndTime, [DurationFlow]=@DurationFlow, [DurationPre]=0, [DurationPost]=0,
            [Fetched]=@Fetched, [Inserted]=@Inserted, [Updated]=@Updated, [Deleted]=@Deleted, [Success]=@Success,
            [FlowRate]=@FlowRate, [NoOfThreads]=@NoOfThreads, [SelectCmd]=@SelectCmd, [InsertCmd]=@InsertCmd,
            [UpdateCmd]=@UpdateCmd, [CreateCmd]=@CreateCmd, [TraceLog]=@TraceLog, [ErrorRuntime]=@ErrorRuntime
        WHERE [FlowID]=@FlowID;

        IF @@ROWCOUNT = 0
        INSERT INTO [flw].[SysLog] ([FlowID],[RunId],[FlowType],[Process],[Batch],[SysAlias],[ExecMode],[StartTime],[EndTime],[DurationFlow],[DurationPre],[DurationPost],[Fetched],[Inserted],[Updated],[Deleted],[Success],[FlowRate],[NoOfThreads],[SelectCmd],[InsertCmd],[UpdateCmd],[CreateCmd],[TraceLog],[ErrorRuntime])
        VALUES (@FlowID,@RunId,@FlowType,@Process,@Batch,@SysAlias,@ExecMode,@StartTime,@EndTime,@DurationFlow,0,0,@Fetched,@Inserted,@Updated,@Deleted,@Success,@FlowRate,@NoOfThreads,@SelectCmd,@InsertCmd,@UpdateCmd,@CreateCmd,@TraceLog,@ErrorRuntime);
        """;
}
