using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The engine's universal backstop for flag combinations the loaders accept (YAML, control-DB, legacy) but the
/// engine refuses: an unimplemented feature, or two settings that contradict each other, fails with a clear
/// message instead of silently doing nothing (or doing half of it). The YAML loader rejects the same cases
/// earlier and is unit-tested; this proves the runner guard that also covers the non-YAML load paths, which
/// never pass through that loader. Skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnsupportedFeatureGuardIntegrationTests
{
    private static IngestionFlow BuildFlow(string src, string trg, VersioningPolicy versioning, bool truncate = false) => new()
    {
        FlowId = 94,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget
        {
            Server = "sink",
            Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg },
            TruncateBeforeLoad = truncate,
        },
        Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
        Versioning = versioning,
    };

    [SkippableFact]
    public async Task InsertUnknownDimensionRow_FailsRunAsNotYetImplemented()
        => await AssertGuardFiresAsync(
            new VersioningPolicy { InsertUnknownDimensionRow = true },
            truncate: false,
            expectedFragments: ["insertUnknownDimensionRow", "not yet implemented"]);

    /// <summary>
    /// TRUNCATE TABLE is not a supported operation on a system-versioned table, so the pair is refused up
    /// front. Legacy skipped the truncate silently, which left the flow author believing a full reload had
    /// happened when the load had appended; failing is the honest behavior.
    /// </summary>
    [SkippableFact]
    public async Task TemporalWithTruncateBeforeLoad_FailsRunWithClearMessage()
        => await AssertGuardFiresAsync(
            new VersioningPolicy { Temporal = new TemporalPolicy { Enabled = true } },
            truncate: true,
            expectedFragments: ["versioning.temporal", "truncateBeforeLoad", "system-versioned"]);

    /// <summary>Two history mechanisms on one table would record every change twice.</summary>
    [SkippableFact]
    public async Task TemporalWithScd2_FailsRunWithClearMessage()
        => await AssertGuardFiresAsync(
            new VersioningPolicy
            {
                Temporal = new TemporalPolicy { Enabled = true },
                Scd2 = new Scd2Policy { Enabled = true },
            },
            truncate: false,
            expectedFragments: ["versioning.temporal", "versioning.scd2"]);

    private static async Task AssertGuardFiresAsync(VersioningPolicy versioning, bool truncate, string[] expectedFragments)
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfGuard_Src";
        const string trg = "_SfGuard_Trg";
        await IntegrationDb.DropTableAsync(cs, src);
        await TemporalDb.DropVersionedTableAsync(cs, trg);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(src, trg, versioning, truncate));

            Assert.False(result.Success);
            foreach (var fragment in expectedFragments)
            {
                Assert.Contains(fragment, result.Error!, StringComparison.OrdinalIgnoreCase);
            }

            // The guard fires before any target work: nothing was created.
            Assert.False(await IntegrationDb.TableExistsAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await TemporalDb.DropVersionedTableAsync(cs, trg);
        }
    }
}
