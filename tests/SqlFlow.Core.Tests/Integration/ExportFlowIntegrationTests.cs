using Parquet;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Export;
using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises export against the real sink, writing to a temp directory: a single-file CSV, month chunking with
/// a NULL-rows file, a Parquet file (read back through Parquet.Net), and the full-mode path loading a flw.Export
/// row and running it through the host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExportFlowIntegrationTests
{
    // The declared database is honored in the generated SQL (three-part reads), so the fixture resolves the
    // sink's real database name instead of inventing one.
    private static async Task<ExportFlow> FlowAsync(string cs, int flowId, string src, string dir, string fileType = "csv") => new()
    {
        FlowId = flowId,
        SysAlias = "exp",
        SrcServer = "sink",
        Source = new RelationalObject
        {
            Database = (await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();"))!,
            Schema = "dbo",
            Name = src,
        },
        ExportBy = "F",
        TrgPath = dir,
        TrgFileName = "out",
        TrgFiletype = fileType,
        AddTimeStampToFileName = false,
        ColumnDelimiter = "|",
    };

    [SkippableFact]
    public async Task SingleFileCsv_WritesHeaderAndRows()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfExp_Src";
        var dir = TempDir();
        await Reset(cs, src, "[Id] int NOT NULL, [Name] nvarchar(20) NULL, [Amount] decimal(10,2) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a',1.5),(2,'b',2.5),(3,'c',3.5);");

        try
        {
            var runner = new ExportFlowRunner(RelationalIngestionHarness.BuildResolver());
            var result = await runner.RunAsync(await FlowAsync(cs, 70, src, dir));

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.TotalRows);
            var file = Path.Combine(dir, "out.csv");
            Assert.True(File.Exists(file));
            var lines = await File.ReadAllLinesAsync(file);
            Assert.Equal("Id|Name|Amount", lines[0]);
            Assert.Equal(4, lines.Length);
            Assert.Equal("1|\"a\"|1.50", lines[1]);
        }
        finally
        {
            await Cleanup(cs, src, dir);
        }
    }

    [SkippableFact]
    public async Task MonthChunking_ProducesMonthFiles_PlusNullRows_ExcludesOutOfWindow()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfExpM_Src";
        var dir = TempDir();
        await Reset(cs, src, "[Id] int NOT NULL, [OrderDate] date NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"""
            INSERT INTO [dbo].[{src}] ([Id],[OrderDate],[Amount]) VALUES
              (1,'2024-01-10',10),(2,'2024-02-15',20),(3,'2024-03-20',30),(4,NULL,40),(5,'2020-01-01',50);
            """);

        try
        {
            var runner = new ExportFlowRunner(RelationalIngestionHarness.BuildResolver());
            var flow = await FlowAsync(cs, 71, src, dir) with { ExportBy = "M", ExportSize = 1, DateColumn = "OrderDate", FromDate = new DateOnly(2024, 1, 1), ToDate = new DateOnly(2024, 3, 31) };

            var result = await runner.RunAsync(flow);

            Assert.True(result.Success, result.Error);
            // 3 in-window rows + 1 NULL-date row; the 2020 row is excluded.
            Assert.Equal(4, result.TotalRows);
            // 3 month files + 1 NullRows file.
            Assert.Equal(4, Directory.GetFiles(dir, "*.csv").Length);
            Assert.True(File.Exists(Path.Combine(dir, "out_2024-01-01-2024-01-31.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "out_NullRows.csv")));
        }
        finally
        {
            await Cleanup(cs, src, dir);
        }
    }

    [SkippableFact]
    public async Task FullExport_WithThreads_ChunksOnThePrimaryKey_AndCoversEveryRow()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfExpFt_Src";
        var dir = TempDir();
        await Reset(cs, src, "[Id] int NOT NULL PRIMARY KEY, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"""
            INSERT INTO [dbo].[{src}] ([Id],[Val])
            SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'v'
            FROM sys.all_objects;
            """);

        try
        {
            var runner = new ExportFlowRunner(RelationalIngestionHarness.BuildResolver());
            var flow = await FlowAsync(cs, 74, src, dir) with { NoOfThreads = 4 };

            var result = await runner.RunAsync(flow);

            Assert.True(result.Success, result.Error);
            Assert.Equal(100, result.TotalRows);
            // Four contiguous key-range files; the NullRows segment is empty (PK) and leaves no file.
            var files = Directory.GetFiles(dir, "*.csv");
            Assert.Equal(4, files.Length);
            Assert.True(File.Exists(Path.Combine(dir, "out_001-024.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "out_075-100.csv")));
            // Every row lands exactly once: 100 data lines + one header per file.
            var lines = 0;
            foreach (var file in files)
            {
                lines += (await File.ReadAllLinesAsync(file)).Length - 1;
            }

            Assert.Equal(100, lines);
        }
        finally
        {
            await Cleanup(cs, src, dir);
        }
    }

    [SkippableFact]
    public async Task FullExport_WithThreads_ButNoUsableKey_FallsBackToOneFile()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfExpFk_Src";
        var dir = TempDir();
        // A keyless heap: no single-column key to chunk on and no incrementalColumn set.
        await Reset(cs, src, "[Name] nvarchar(20) NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES ('a',1),('b',2),('c',3);");

        try
        {
            var runner = new ExportFlowRunner(RelationalIngestionHarness.BuildResolver());
            var flow = await FlowAsync(cs, 75, src, dir) with { NoOfThreads = 4 };

            var result = await runner.RunAsync(flow);

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.TotalRows);
            Assert.Equal(["out.csv"], Directory.GetFiles(dir, "*.csv").Select(f => Path.GetFileName(f)!).ToArray());
        }
        finally
        {
            await Cleanup(cs, src, dir);
        }
    }

    [SkippableFact]
    public async Task Parquet_WritesReadableFile()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfExpP_Src";
        var dir = TempDir();
        await Reset(cs, src, "[Id] int NOT NULL, [Name] nvarchar(20) NULL, [Amount] decimal(10,2) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a',1.5),(2,'b',2.5);");

        try
        {
            var runner = new ExportFlowRunner(RelationalIngestionHarness.BuildResolver());
            var result = await runner.RunAsync(await FlowAsync(cs, 73, src, dir, fileType: "parquet"));

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.TotalRows);
            var file = Path.Combine(dir, "out.parquet");
            Assert.True(File.Exists(file));

            await using var stream = File.OpenRead(file);
            using var parquet = await ParquetReader.CreateAsync(stream);
            Assert.Equal(["Id", "Name", "Amount"], parquet.Schema.DataFields.Select(f => f.Name).ToArray());
            using var rowGroup = parquet.OpenRowGroupReader(0);
            Assert.Equal(2, rowGroup.RowCount);
        }
        finally
        {
            await Cleanup(cs, src, dir);
        }
    }

    [SkippableFact]
    public async Task FullMode_LoadsExportFlow_RunsAndLogs()
    {
        const int flowId = 72;
        var cs = IntegrationDb.Require();
        await ControlPlaneSchema.EnsureAsync(cs);
        const string src = "_SfExpFm_Src";
        var dir = TempDir();
        await Reset(cs, src, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");
        await CleanControl(cs, flowId);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[CredentialProfile] ([ProfileAlias],[Mode]) VALUES ('sf_cp_exp','InlineConnectionString');");
            await IntegrationDb.ExecuteAsync(cs, "INSERT INTO [flw].[DataSource] ([Alias],[Kind],[ConnectionRef],[CredentialProfileID]) VALUES ('sf_exp_sink','MSSQL','${env:SQLFlowSinkConStr}',(SELECT [CredentialProfileID] FROM [flw].[CredentialProfile] WHERE [ProfileAlias]='sf_cp_exp'));");
            var dbName = await IntegrationDb.ScalarAsync<string>(cs, "SELECT DB_NAME();");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [flw].[Export] ([FlowID],[SysAlias],[srcServer],[srcDBSchTbl],[ExportBy],[trgPath],[trgFileName],[trgFiletype],[AddTimeStampToFileName]) " +
                $"VALUES ({flowId},'exp-flow','sf_exp_sink','[{dbName}].[dbo].[{src}]','F','{dir.Replace("'", "''", StringComparison.Ordinal)}','fm','csv',0);");

            var host = FullModeIngestionHost.Create(new FullModeOptions { ControlConnectionString = cs });
            var result = await host.RunExportByIdAsync(flowId);

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.TotalRows);
            Assert.True(File.Exists(Path.Combine(dir, "fm.csv")));
            Assert.Equal("exp", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [FlowType] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await Cleanup(cs, src, dir);
            await CleanControl(cs, flowId);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfexp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task Reset(string cs, string src, string columns)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({columns});");
    }

    private static async Task Cleanup(string cs, string src, string dir)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static Task CleanControl(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.Export','U') IS NOT NULL DELETE FROM [flw].[Export] WHERE [FlowID] = {flowId}; " +
            "IF OBJECT_ID('flw.DataSource','U') IS NOT NULL DELETE FROM [flw].[DataSource] WHERE [Alias] = 'sf_exp_sink'; " +
            "IF OBJECT_ID('flw.CredentialProfile','U') IS NOT NULL DELETE FROM [flw].[CredentialProfile] WHERE [ProfileAlias] = 'sf_cp_exp'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");
}
