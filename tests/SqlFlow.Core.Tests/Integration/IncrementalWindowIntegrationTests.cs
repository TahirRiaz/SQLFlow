using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the incremental read window against the real sink: the first run into an empty target is a full
/// load; subsequent runs bound the source read to the changed slice using the target watermark. Asserts the
/// generated WHERE string (surfaced on the result), not just row counts, so the legacy precedence is verified
/// exactly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalWindowIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg, IncrementalPolicy incremental, IngestionSource? sourceOverride = null)
        => new()
        {
            FlowId = flowId,
            Source = sourceOverride ?? new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            Incremental = incremental,
        };

    [SkippableFact]
    public async Task EmptyTarget_FullLoad_ThenWatermark_StagesOnlyNewRows()
    {
        const int flowId = 11;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc1_Src";
        const string trg = "_SfInc1_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.True(first.RunFullLoad);
            Assert.Equal(string.Empty, first.SourceWhere);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'d'),(5,'e');");

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.False(second.RunFullLoad);
            Assert.Equal(" AND [Id] > 3", second.SourceWhere);
            Assert.Equal(2, second.RowsStaged);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task DateColumn_AppliesOverlapDays()
    {
        const int flowId = 12;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc2_Src";
        const string trg = "_SfInc2_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'2026-01-10'),(2,'2026-01-20');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { DateColumn = "OrderDate", OverlapDays = 7 });

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            // MAX(OrderDate)=2026-01-20, minus 7 overlap days => 2026-01-13. Only Id=2 (01-20) is re-read.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(" AND [OrderDate] > '2026-01-13 00:00:00.000'", second.SourceWhere);
            Assert.Equal(1, second.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task IncrementalColumn_WinsOverDateColumn()
    {
        const int flowId = 13;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc3_Src";
        const string trg = "_SfInc3_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'2026-01-10'),(2,'2026-01-20');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], DateColumn = "OrderDate", OverlapDays = 7 });

            Assert.True((await runner.RunAsync(flow)).Success);

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            // The incremental predicate wins; the date predicate is suppressed entirely.
            Assert.Equal(" AND [Id] > 2", second.SourceWhere);
            Assert.DoesNotContain("OrderDate", second.SourceWhere, StringComparison.Ordinal);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FullLoadFlag_ReadsWholeSource()
    {
        const int flowId = 14;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc4_Src";
        const string trg = "_SfInc4_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], FullLoad = true });

            Assert.True((await runner.RunAsync(flow)).Success);

            // FullLoad flag re-reads the whole source (empty WHERE) and reconciles via the keyed upsert.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(string.Empty, second.SourceWhere);
            Assert.Equal(3, second.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task ReplaceFilter_And_AppendFilter()
    {
        const int flowId = 15;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc5_Src";
        const string trg = "_SfInc5_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Region] nvarchar(10) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'EU'),(2,'US'),(3,'EU');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var baseFlow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // Establish the watermark (MAX(Id)=3) with a plain full first load.
            Assert.True((await runner.RunAsync(baseFlow)).Success);

            // Replace mode: the user filter replaces the computed incremental predicate entirely.
            var replace = baseFlow with
            {
                Source = baseFlow.Source with { Filter = "AND [Region] = 'EU'", FilterIsAppend = false },
            };
            var replaceResult = await runner.RunAsync(replace);
            Assert.True(replaceResult.Success, replaceResult.Error);
            Assert.Equal(" AND [Region] = 'EU'", replaceResult.SourceWhere);

            // Append mode: the incremental predicate then the user filter.
            var append = baseFlow with
            {
                Source = baseFlow.Source with { Filter = "AND [Region] = 'EU'", FilterIsAppend = true },
            };
            var appendResult = await runner.RunAsync(append);
            Assert.True(appendResult.Success, appendResult.Error);
            Assert.Equal(" AND [Id] > 3 AND [Region] = 'EU'", appendResult.SourceWhere);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FullLoad_NonEmptyTarget_NullWatermark_DoesNotDuplicate()
    {
        // Regression: a NULL watermark on a NON-empty target forces a full reload; the keyed apply must
        // anti-join (insert only missing keys), never blindly re-insert existing rows.
        const int flowId = 19;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc6_Src";
        const string trg = "_SfInc6_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL, [ModifiedDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a',NULL),(2,'b',NULL),(3,'c',NULL);");

        // Pre-populate the target (matching the evolved schema) with rows whose watermark column is NULL.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL, [ModifiedDate] datetime2(3) NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[Val]) VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { DateColumn = "ModifiedDate" });

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.True(result.RunFullLoad);
            Assert.Equal(string.Empty, result.SourceWhere);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task DownstreamWatermark_AnchorsToDownstreamTable_RepullsDeletedRows()
    {
        // The opt-in downstream anchor: a chained pre -> ods flow reads its high-water MAX from the ods (silver)
        // table, not its own pre target. Deleting rows from the ods table lowers the watermark, so the source rows
        // are re-pulled automatically on the next run (the self-healing backfill this feature exists for).
        const int flowId = 20;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc7_Src";
        const string pre = "_SfInc7_Pre";
        const string ods = "_SfInc7_Ods";
        await Reset(cs, src, pre, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.DropTableAsync(cs, ods);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d'),(5,'e');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, pre, new IncrementalPolicy { Columns = ["Id"] });

            // First run with no downstream table resolved (a CLI run, or before the ods table exists): the flow
            // behaves exactly as normal and full-loads Id 1..5 into the pre table (MAX 5).
            Assert.True((await runner.RunAsync(flow)).Success);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, pre));

            // The downstream ods table holds only Id 1..3 (its rows 4,5 were deleted). MAX(ods.Id) = 3.
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{ods}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{ods}] VALUES (1,'a'),(2,'b'),(3,'c');");
            var silver = new RelationalObject { Database = "db", Schema = "dbo", Name = ods };

            // Anchored to the downstream table, the watermark is MAX(ods.Id)=3, NOT MAX(pre.Id)=5, so the deleted
            // rows 4,5 are read again from the source.
            var second = await runner.RunAsync(flow, new IngestionRunOptions { WatermarkSourceTable = silver });
            Assert.True(second.Success, second.Error);
            Assert.Equal(" AND [Id] > 3", second.SourceWhere);
            Assert.Equal(2, second.RowsStaged);
            Assert.NotNull(second.Incremental);
            Assert.StartsWith("downstream MAX", second.Incremental!.WatermarkSource);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, ods);
            await Cleanup(cs, src, pre, flowId);
        }
    }

    [SkippableFact]
    public async Task DownstreamWatermark_FallsBackToOwnTarget_WhenDownstreamLacksTheColumnOrIsAbsent()
    {
        // Safety: the anchor is column-safe and reachability-safe. A downstream table that renamed/dropped the
        // watermark column, or that is not reachable at all, must fall back to probing the flow's own target,
        // never probe a MAX over a missing column (which would fail) or read an absent object (which would be
        // mistaken for an empty target and force a full reload).
        const int flowId = 21;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc8_Src";
        const string pre = "_SfInc8_Pre";
        const string bad = "_SfInc8_Bad";
        await Reset(cs, src, pre, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.DropTableAsync(cs, bad);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d'),(5,'e');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, pre, new IncrementalPolicy { Columns = ["Id"] });

            // Establish the pre watermark (MAX(pre.Id) = 5).
            Assert.True((await runner.RunAsync(flow)).Success);

            // A downstream table that does NOT carry the [Id] watermark column (it was renamed to [Sid]).
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{bad}] ([Sid] int NOT NULL, [Val] nvarchar(20) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{bad}] VALUES (1,'a');");
            var renamed = new RelationalObject { Database = "db", Schema = "dbo", Name = bad };

            var missingColumn = await runner.RunAsync(flow, new IngestionRunOptions { WatermarkSourceTable = renamed });
            Assert.True(missingColumn.Success, missingColumn.Error);
            Assert.Equal(" AND [Id] > 5", missingColumn.SourceWhere);
            Assert.StartsWith("target MAX", missingColumn.Incremental!.WatermarkSource);

            // An unreachable/absent downstream table falls back the same way (never forces a full reload).
            var absent = new RelationalObject { Database = "db", Schema = "dbo", Name = "_SfInc8_DoesNotExist" };
            var missingTable = await runner.RunAsync(flow, new IngestionRunOptions { WatermarkSourceTable = absent });
            Assert.True(missingTable.Success, missingTable.Error);
            Assert.False(missingTable.RunFullLoad);
            Assert.Equal(" AND [Id] > 5", missingTable.SourceWhere);
            Assert.StartsWith("target MAX", missingTable.Incremental!.WatermarkSource);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, bad);
            await Cleanup(cs, src, pre, flowId);
        }
    }

    [SkippableFact]
    public async Task ReprocessFromSourceMin_RunOverride_WidensToSourceMin_AndRepullsBackDatedRows()
    {
        // The run-time form of fetchMinValuesFromSource, which a group backfill sets on the anchor's descendants: a
        // back-dated row landed in the source (Id below the target's MAX) would be filtered out by the normal
        // MAX-from-target watermark. The override probes MIN over the source instead, widens the window back to that
        // minimum (op flips to >=), and re-pulls the back-dated row so the backfill flows through instead of
        // stopping below the high-water mark.
        const int flowId = 22;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc9_Src";
        const string trg = "_SfInc9_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (3,'c'),(4,'d'),(5,'e');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // Establish the target watermark: MAX(trg.Id) = 5.
            Assert.True((await runner.RunAsync(flow)).Success);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // A back-dated row lands in the source below the target's MAX (Id 1 < 5). The normal watermark would
            // filter it out ([Id] > 5 selects nothing); the override re-pulls it.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

            var reprocess = await runner.RunAsync(
                flow, new IngestionRunOptions { Parameters = new RunParameters { ReprocessFromSourceMin = true } });
            Assert.True(reprocess.Success, reprocess.Error);
            Assert.Equal(" AND [Id] >= 1", reprocess.SourceWhere);
            Assert.NotNull(reprocess.Incremental);
            Assert.StartsWith("source MIN", reprocess.Incremental!.WatermarkSource);
            // The back-dated row is now in the target (4 rows: the original 3,4,5 plus the re-pulled 1).
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task ReprocessFromSourceMin_RunOverride_UsesSourceMin_EvenWhenNotBelowTarget()
    {
        // An explicit operator backfill must bound at the source minimum unconditionally, even when the source holds
        // nothing older than the target's high-water mark (the case a force-re-land upstream produces: staging is all
        // "fresh"). The DECLARED fetchMinValuesFromSource would keep the target MAX here as an optimization; the run
        // override does not, because the operator asked to reprocess and must see the source-min bound, not a silent
        // fall-back to the target.
        const int flowId = 23;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc10_Src";
        const string trg = "_SfInc10_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // Establish the target watermark: MAX(trg.Id) = 3.
            Assert.True((await runner.RunAsync(flow)).Success);

            // The source now holds ONLY rows at or above the target's MAX (Id 3..5): source MIN (3) is not below
            // target MAX (3), so the legacy guard would fall back to target MAX. The override still bounds at MIN.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'d'),(5,'e');");
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE [Id] < 3;");

            var reprocess = await runner.RunAsync(
                flow, new IngestionRunOptions { Parameters = new RunParameters { ReprocessFromSourceMin = true } });
            Assert.True(reprocess.Success, reprocess.Error);
            Assert.Equal(" AND [Id] >= 3", reprocess.SourceWhere);
            Assert.NotNull(reprocess.Incremental);
            Assert.StartsWith("source MIN", reprocess.Incremental!.WatermarkSource);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task NumericColumn_AppliesLookback()
    {
        // The numeric counterpart of DateColumn_AppliesOverlapDays: the watermark is rewound by Lookback
        // before it bounds the read, so the predicate is MAX - lookback rather than the bare MAX.
        const int flowId = 24;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc11_Src";
        const string trg = "_SfInc11_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (10,'a'),(20,'b'),(30,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], Lookback = 15 });

            Assert.True((await runner.RunAsync(flow)).Success);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // MAX(Id)=30, minus a lookback of 15 => 15. Only Id 20 and 30 fall in the re-read window.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(" AND [Id] > 15", second.SourceWhere);
            Assert.Equal(2, second.RowsStaged);

            // Re-reading is idempotent: the keyed upsert matches both rows, so nothing is duplicated.
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task NumericLookback_RecoversRowThatCommittedBelowTheWatermark()
    {
        // The bug this exists for. An auto-increment id is allocated at INSERT but the row becomes visible at
        // COMMIT, so a reader can see id N+k while N is still in flight. A bare MAX watermark advances past N
        // and the next run's strict > can never reach back down to it: the row is skipped permanently.
        // Id 4 below stands in for that late-committing row, inserted after the watermark has already moved to 5.
        const int flowId = 25;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc12_Src";
        const string trg = "_SfInc12_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c'),(5,'e');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var noLookback = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // The first run sees 1,2,3,5 and parks the watermark at 5, having jumped over the in-flight 4.
            Assert.True((await runner.RunAsync(noLookback)).Success);
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));

            // The straggler commits.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'d');");

            // Without a lookback the read is bounded at > 5, so Id 4 is invisible to every future run.
            var bare = await runner.RunAsync(noLookback);
            Assert.True(bare.Success, bare.Error);
            Assert.Equal(" AND [Id] > 5", bare.SourceWhere);
            Assert.Equal(0, bare.RowsStaged);
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 4"));

            // With a lookback the window reaches back below the high-water mark and the row is recovered.
            var withLookback = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], Lookback = 3 });
            var healed = await runner.RunAsync(withLookback);
            Assert.True(healed.Success, healed.Error);
            Assert.Equal(" AND [Id] > 2", healed.SourceWhere);
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 4"));

            // The rows already held (3 and 5) were re-read too and must not have been duplicated.
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task NumericLookback_IsIgnoredForANonArithmeticWatermarkColumn()
    {
        // A high-water column can be a string (the NeTEx-style keys in the estate) or binary. Subtracting from
        // its MAX would be invalid SQL, so the lookback is skipped for those types rather than failing the run.
        const int flowId = 26;
        var cs = IntegrationDb.Require();
        const string src = "_SfInc13_Src";
        const string trg = "_SfInc13_Trg";
        await Reset(cs, src, trg, flowId, "[Id] nvarchar(40) NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES ('NSR:Line:1','a'),('NSR:Line:2','b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"], Lookback = 10 });

            Assert.True((await runner.RunAsync(flow)).Success);

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(" AND [Id] > 'NSR:Line:2'", second.SourceWhere);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task Cleanup(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
