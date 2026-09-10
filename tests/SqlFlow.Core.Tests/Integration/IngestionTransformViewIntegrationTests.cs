using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The pre-ingestion transform view post-process on the RELATIONAL path (the external-database landing
/// contract): after the staged upsert, the runner refreshes the typed transformation view over the target so
/// the downstream chained flow reads correctly-typed data. Declared transforms need no inference service, which
/// is exactly how the harness runner is wired.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionTransformViewIntegrationTests
{
    private const int FlowId = 7411;

    [SkippableFact]
    public async Task RelationalRun_WithDeclaredTransforms_RefreshesTypedViewOverTarget()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTvw_Src";
        const string trg = "_SfTvw_Trg";
        // Transform views are named v_<table> (the original SQLFlow pre-view convention).
        const string view = "v_" + trg;

        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);

        // The landing shape of an external-DB flow: string-typed source columns (foreign types are unreliable,
        // so the pre table lands text and the view types it).
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Amount] varchar(50) NULL, [VehicleType] varchar(255) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([Id],[Amount],[VehicleType]) VALUES (1, '10.50', 'truck'), (2, '20.00', 'van');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                Transform = new TypeInferencePolicy
                {
                    Columns =
                    [
                        new ColumnTransform { Name = "Amount", Type = "decimal(18,2)" },
                        new ColumnTransform { Name = "VehicleType", Expression = "CAST(@ColName AS varchar(50))", Type = "varchar(50)", Alias = "VehicleTypeClean" },
                    ],
                },
            };

            var result = await runner.RunAsync(flow);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.TransformView);
            Assert.Equal(view, result.TransformView!.ViewName);
            Assert.Contains(result.SqlTrace, t => t.Step == "transform.view");

            // The view exists over the target and carries the declared types/renames; untouched columns pass
            // through (including the system columns the target grew).
            Assert.Equal(2L, await IntegrationDb.ScalarAsync<long>(cs, $"SELECT COUNT_BIG(*) FROM [dbo].[{view}];"));
            Assert.Equal("decimal", await IntegrationDb.ScalarAsync<string>(cs,
                $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{view}' AND COLUMN_NAME = 'Amount';"));
            Assert.Equal("varchar", await IntegrationDb.ScalarAsync<string>(cs,
                $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{view}' AND COLUMN_NAME = 'VehicleTypeClean';"));
            Assert.Equal("Id", await IntegrationDb.ScalarAsync<string>(cs,
                $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{view}' AND COLUMN_NAME = 'Id';"));

            // Re-run: CREATE OR ALTER refreshes the same view without error (idempotent chain execution).
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.NotNull(second.TransformView);
        }
        finally
        {
            await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
        }
    }
}
