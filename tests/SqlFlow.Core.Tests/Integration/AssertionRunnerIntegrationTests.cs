using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises data-quality assertions against the real sink: macro substitution of @TableName / @FilterCriteria,
/// the first-row/first-two-columns result reading, the non-blocking failure path (one bad assertion does not
/// stop the others and never fails the run), unknown-name dropping, and the flow of results through
/// IngestionRunResult.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssertionRunnerIntegrationTests
{
    private const string EmptyTableExp = "SELECT COUNT(*) FROM @TableName WHERE 1=1 @FilterCriteria";
    private const string FreshnessExp = "SELECT DATEDIFF(DAY, MAX([LoadDate]), GETDATE()), MAX([LoadDate]) FROM @TableName WHERE 1=1 @FilterCriteria";
    private const string BadExp = "SELECT [NoSuchColumn] FROM @TableName";

    private static IngestionFlow Flow(string trg, IReadOnlyList<string> assertions, string? filter = null)
        => new()
        {
            FlowId = 30,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = "ignored" }, IncrementalClause = filter },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Assertions = assertions,
        };

    [SkippableFact]
    public async Task RunsAssertions_SubstitutesMacros_AndReadsResults()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfAssert_Trg";
        await SetupTarget(cs, trg);

        try
        {
            var runner = new AssertionRunner(Store());

            var results = await runner.RunAsync(Flow(trg, ["CheckEmptyTable", "CheckFreshnessDaily"]), cs);

            Assert.Equal(2, results.Count);
            Assert.Equal("CheckEmptyTable", results[0].Name);
            Assert.True(results[0].Evaluated);
            Assert.Equal("3", results[0].Result);
            Assert.Equal("CheckFreshnessDaily", results[1].Name);
            Assert.True(results[1].Evaluated);
            Assert.NotEqual(string.Empty, results[1].AssertedValue);

            // @FilterCriteria substitution: AND [Id] > 1 leaves 2 rows.
            var filtered = await runner.RunAsync(Flow(trg, ["CheckEmptyTable"], filter: "AND [Id] > 1"), cs);
            Assert.Equal("2", Assert.Single(filtered).Result);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }

    [SkippableFact]
    public async Task BadAssertion_IsNonBlocking_AndUnknownNameIsDropped()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfAssertBad_Trg";
        await SetupTarget(cs, trg);

        try
        {
            var runner = new AssertionRunner(Store());

            var results = await runner.RunAsync(Flow(trg, ["CheckEmptyTable", "BadCheck", "DoesNotExist"]), cs);

            // Unknown name dropped; the bad one is recorded but does not stop the good one.
            Assert.Equal(2, results.Count);
            Assert.True(results[0].Evaluated);
            Assert.Equal("BadCheck", results[1].Name);
            Assert.False(results[1].Evaluated);
            Assert.Equal("0", results[1].Result);
            Assert.NotNull(results[1].Error);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }

    [SkippableFact]
    public async Task FullRun_FlowsAssertionsThroughResult_AndStaysSuccessful()
    {
        const int flowId = 31;
        var cs = IntegrationDb.Require();
        const string src = "_SfAssertFull_Src";
        const string trg = "_SfAssertFull_Trg";
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunnerWithAssertions(new AssertionRunner(Store()));
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                Assertions = ["CheckEmptyTable", "BadCheck"],
            };

            var result = await runner.RunAsync(flow);

            // Assertions are non-blocking: the run succeeds even though one assertion failed.
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.Assertions.Count);
            Assert.True(result.Assertions[0].Evaluated);
            Assert.Equal("2", result.Assertions[0].Result);
            Assert.False(result.Assertions[1].Evaluated);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task ManualModeAssertion_IsSkippedByDefault_AndRunsWhenIncluded()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfAssertMode_Trg";
        await SetupTarget(cs, trg);

        try
        {
            var runner = new AssertionRunner(Store());
            var flow = Flow(trg, ["CheckEmptyTable", "ManualCheck"]);

            // An automatic ingestion run evaluates only the auto-mode assertions.
            var automatic = await runner.RunAsync(flow, cs);
            Assert.Equal("CheckEmptyTable", Assert.Single(automatic).Name);

            // The on-demand assertions-only run evaluates the whole list, manual included, in declaration order.
            var onDemand = await runner.RunAsync(flow, cs, includeManual: true);
            Assert.Equal(2, onDemand.Count);
            Assert.Equal("ManualCheck", onDemand[1].Name);
            Assert.True(onDemand[1].Evaluated);
            Assert.Equal("3", onDemand[1].Result);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }

    [SkippableFact]
    public async Task AssertionsOnlyRun_EvaluatesWholeList_AndReadsNoSource()
    {
        const int flowId = 32;
        var cs = IntegrationDb.Require();
        const string trg = "_SfAssertOnly_Trg";
        await SetupTarget(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        try
        {
            var runner = RelationalIngestionHarness.BuildRunnerWithAssertions(new AssertionRunner(Store()));
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                // The source deliberately does not exist: an assertions-only run must never touch it.
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = "_SfAssertOnly_NoSuchSource" } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Assertions = ["CheckEmptyTable", "ManualCheck"],
            };

            var result = await runner.RunAsync(
                flow, new IngestionRunOptions { Parameters = new RunParameters { AssertionsOnly = true } });

            Assert.True(result.Success, result.Error);
            Assert.Equal(0, result.RowsStaged);
            Assert.Equal(2, result.Assertions.Count); // the on-demand run includes the manual-mode assertion
            Assert.All(result.Assertions, a => Assert.True(a.Evaluated));
            Assert.Equal("3", result.Assertions[0].Result); // the target's pre-existing rows, untouched
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    private static async Task SetupTarget(string cs, string trg)
    {
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL, [LoadDate] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[LoadDate]) VALUES (1, SYSUTCDATETIME()), (2, SYSUTCDATETIME()), (3, SYSUTCDATETIME());");
    }

    private static FakeStore Store() => new();

    private sealed class FakeStore : IAssertionDefinitionStore
    {
        private static readonly IReadOnlyDictionary<string, AssertionDefinition> All =
            new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["CheckEmptyTable"] = new() { Name = "CheckEmptyTable", Expression = EmptyTableExp },
                ["CheckFreshnessDaily"] = new() { Name = "CheckFreshnessDaily", Expression = FreshnessExp },
                ["BadCheck"] = new() { Name = "BadCheck", Expression = BadExp },
                ["ManualCheck"] = new() { Name = "ManualCheck", Expression = EmptyTableExp, Mode = ExecutionMode.Manual },
            };

        public Task<IReadOnlyDictionary<string, AssertionDefinition>> ResolveAsync(IEnumerable<string> names, CancellationToken ct = default)
        {
            var resolved = new Dictionary<string, AssertionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (All.TryGetValue(name, out var definition))
                {
                    resolved[name] = definition;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, AssertionDefinition>>(resolved);
        }
    }
}
