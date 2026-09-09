using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The pre-ingestion transform view post-process against the physical sink: a CSV flow loads its raw
/// (all-varchar) landing table, then refreshes the typed transformation view over it - inference typing the
/// columns no authored transform names, authored transforms overriding where declared. Downstream chained flows
/// read this view, so its existence and its column types ARE the contract.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TransformViewIntegrationTests
{
    private static FlowDefinition Flow(string cs, string table, string csvPath, TypeInferencePolicy inference) => new()
    {
        Name = "it-transform-view-" + table,
        Source = new SourceSpec { Type = "csv", Location = csvPath },
        Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
        Inference = inference,
    };

    private static async Task DropViewAsync(string cs, string view)
        => await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");

    private static Task<string?> ViewColumnTypeAsync(string cs, string view, string column)
        => IntegrationDb.ScalarAsync<string>(cs,
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS " +
            $"WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{view}' AND COLUMN_NAME = '{column}';");

    [SkippableFact]
    public async Task Run_WithInferenceAndDeclaredTransform_RefreshesTypedView()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_TView_" + Guid.NewGuid().ToString("N")[..8];
        var view = "v_" + table;
        await IntegrationDb.DropTableAsync(cs, table);
        await DropViewAsync(cs, view);

        var dir = Directory.CreateTempSubdirectory("sqlflow-tview-");
        try
        {
            var csv = Path.Combine(dir.FullName, "orders.csv");
            await File.WriteAllTextAsync(csv,
                """
                order_id,amount,order_date,vehicle_type
                1,10.50,2026-01-15,truck
                2,20.00,2026-02-20,van
                3,7.25,2026-03-05,truck
                """);

            var flow = Flow(cs, table, csv, new TypeInferencePolicy
            {
                Enabled = true,
                Columns =
                [
                    // The authored transform must WIN over inference for its column and rename the output.
                    new ColumnTransform
                    {
                        Name = "vehicle_type",
                        Expression = "CAST(@ColName AS varchar(50))",
                        Type = "varchar(50)",
                        Alias = "vehicle_type_clean",
                    },
                ],
            });

            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
            Assert.NotNull(result.TransformView);
            Assert.Equal(view, result.TransformView!.ViewName);
            Assert.Contains(result.TransformView.Columns, c => c.ColumnName == "vehicle_type_clean");
            Assert.Contains("CREATE OR ALTER VIEW [dbo].[" + view + "]", result.TransformView.Ddl, StringComparison.Ordinal);

            // The run's statement trace records every SQL statement it executed, in order, so the node streams
            // them live and the Statements view is populated at completion. The first run creates the table, so it
            // carries the schema DDL AND the view refresh; the traced view statement is exactly the view DDL, and
            // sequences are contiguous from 1.
            Assert.Contains(result.SqlTrace, t => t.Step == "schema.apply-ddl");
            var viewEntry = Assert.Single(result.SqlTrace, t => t.Step == "transform.view");
            Assert.Equal(result.TransformView.Ddl, viewEntry.Sql);
            Assert.Equal(Enumerable.Range(1, result.SqlTrace.Count), result.SqlTrace.Select(e => e.Sequence));

            // The view exists, is queryable, and carries the typed projection: the declared cast renames and
            // types vehicle_type, and inference types the numeric/date columns the author never named.
            Assert.Equal(3L, await IntegrationDb.ScalarAsync<long>(cs, $"SELECT COUNT_BIG(*) FROM [dbo].[{view}];"));
            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "vehicle_type_clean"));

            var amountType = await ViewColumnTypeAsync(cs, view, "amount");
            Assert.True(amountType is "decimal" or "numeric", $"amount should infer numeric, was '{amountType}'");
            var dateType = await ViewColumnTypeAsync(cs, view, "order_date");
            Assert.True(dateType is "date" or "datetime2", $"order_date should infer a date type, was '{dateType}'");

            // Re-running is idempotent: CREATE OR ALTER refreshes the same view without error.
            var second = await IntegrationDb.RealRunner().RunAsync(flow);
            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.NotNull(second.TransformView);

            // The re-run changes no schema (the table already exists), so it executes no schema DDL - exactly the
            // case that previously left the Statements view empty. The view refresh still runs every time, so the
            // trace is never empty: it carries the transform.view statement.
            Assert.DoesNotContain(second.SqlTrace, t => t.Step == "schema.apply-ddl");
            Assert.Contains(second.SqlTrace, t => t.Step == "transform.view");
        }
        finally
        {
            await DropViewAsync(cs, view);
            await IntegrationDb.DropTableAsync(cs, table);
            dir.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public async Task Run_WithGenerateViewOff_CreatesNoView()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_TViewOff_" + Guid.NewGuid().ToString("N")[..8];
        var view = "v_" + table;
        await IntegrationDb.DropTableAsync(cs, table);
        await DropViewAsync(cs, view);

        var dir = Directory.CreateTempSubdirectory("sqlflow-tview-");
        try
        {
            var csv = Path.Combine(dir.FullName, "orders.csv");
            await File.WriteAllTextAsync(csv, "a,b\n1,2\n");

            var flow = Flow(cs, table, csv, new TypeInferencePolicy { Enabled = true, GenerateView = false });
            var result = await IntegrationDb.RealRunner().RunAsync(flow);

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Null(result.TransformView);
            Assert.Null(await IntegrationDb.ScalarAsync<string>(cs,
                $"SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID('dbo') AND name = '{view}';"));
        }
        finally
        {
            await DropViewAsync(cs, view);
            await IntegrationDb.DropTableAsync(cs, table);
            dir.Delete(recursive: true);
        }
    }
}
