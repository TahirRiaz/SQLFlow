using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The load profile is what the control plane hands an assistant or an operator instead of the raw YAML, so its
/// statements must be exactly what the engine does. These cases are the production shapes that were misread when
/// the profile did not exist: a keyed full read mistaken for an incremental load because it had an upsert key, and
/// a failing flow whose last success went unreported. The YAML mirrors the Reisefrihet flows in dwh-pipelines-prod.
/// </summary>
public sealed class FlowLoadProfilesTests
{
    private const string Connections = """
        connections:
          src:
            provider: mysql
            connection: ${keyvault:secrets/src}
          ods: ${env:ODS}
        """;

    [Fact]
    public void KeyedIngestionWithoutIncremental_IsAFullReadThatUpserts()
    {
        var profile = FlowLoadProfiles.FromYaml($"""
            flowType: ing
            name: reisefrihet_ticket_02_ing
            {Connections}
            source:
              server: src
              object: "[data_warehouse_export].[data_warehouse_export].[ticket]"
            target:
              server: ods
              object: "[dw-dwh-prod].[arc].[Reisefrihet_Ticket]"
            load:
              keyColumns: [id]
              batchUpsert: true
              batchUpsertRowCount: 500000
            schema:
              sync: false
            """);

        // The upsert key is not a watermark: the read is full, the apply is keyed.
        Assert.Equal(FlowReadModes.Full, profile.ReadMode);
        Assert.Equal(["id"], profile.KeyColumns);
        Assert.Empty(profile.WatermarkColumns);
        Assert.False(profile.ReplacesTargetEachRun);
        Assert.Contains("whole source table on every run", profile.Read, StringComparison.Ordinal);
        Assert.Contains("no incremental watermark is declared", profile.Read, StringComparison.Ordinal);
        Assert.Contains("upserts into [dw-dwh-prod].[arc].[Reisefrihet_Ticket] on id", profile.Write, StringComparison.Ordinal);
        Assert.Contains("batches of 500000 rows", profile.Write, StringComparison.Ordinal);
        Assert.Contains("never truncated", profile.Write, StringComparison.Ordinal);
        Assert.StartsWith("Reads the whole source table", profile.Summary, StringComparison.Ordinal);
        Assert.EndsWith(".", profile.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalColumnsWithLookback_NamesTheWatermarkAndTheRewind()
    {
        var profile = FlowLoadProfiles.FromYaml($"""
            flowType: ing
            name: reisefrihet_deviceinfo_02_ing
            {Connections}
            source:
              server: src
              object: "[db].[db].[device_info]"
            target:
              server: ods
              object: "[dw-dwh-prod].[arc].[Reisefrihet_DeviceInfo]"
            load:
              keyColumns: [id]
            incremental:
              columns: [id]
              lookback: 500000
            """);

        Assert.Equal(FlowReadModes.Incremental, profile.ReadMode);
        Assert.Equal(["id"], profile.WatermarkColumns);
        Assert.Equal(["id"], profile.KeyColumns);
        Assert.Contains("id greater than the high-water mark of the target", profile.Read, StringComparison.Ordinal);
        Assert.Contains("rewound by 500000", profile.Read, StringComparison.Ordinal);
        Assert.Contains("an empty target is loaded in full", profile.Read, StringComparison.Ordinal);
    }

    [Fact]
    public void DateColumnWatermark_ReportsTheOverlap()
    {
        var profile = FlowLoadProfiles.FromYaml($"""
            flowType: ing
            name: orders_02_ing
            {Connections}
            source:
              server: src
              object: "[db].[dbo].[orders]"
            target:
              server: ods
              object: "[dw].[arc].[Orders]"
            load:
              keyColumns: [order_id]
            incremental:
              dateColumn: updated_time
              overlapDays: 3
            """);

        Assert.Equal(FlowReadModes.Incremental, profile.ReadMode);
        Assert.Equal(["updated_time"], profile.WatermarkColumns);
        Assert.Contains("updated_time greater than the high-water mark of the target minus 3 day(s)", profile.Read, StringComparison.Ordinal);
    }

    [Fact]
    public void FullLoadFlag_OverridesADeclaredWatermark()
    {
        var profile = FlowLoadProfiles.FromYaml($"""
            flowType: ing
            name: orders_02_ing
            {Connections}
            source:
              server: src
              object: "[db].[dbo].[orders]"
            target:
              server: ods
              object: "[dw].[arc].[Orders]"
            load:
              keyColumns: [order_id]
            incremental:
              columns: [order_id]
              fullLoad: true
            """);

        // The engine's precedence: fullLoad discards the watermark, so the profile must not report one.
        Assert.Equal(FlowReadModes.Full, profile.ReadMode);
        Assert.Empty(profile.WatermarkColumns);
        Assert.Contains("declared watermark (order_id) is ignored", profile.Read, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateBeforeLoadWithoutKey_ReplacesTheTarget()
    {
        var profile = FlowLoadProfiles.FromYaml($"""
            flowType: ing
            name: dim_02_ing
            {Connections}
            source:
              server: src
              object: "[db].[dbo].[dim]"
            target:
              server: ods
              object: "[dw].[arc].[Dim]"
              truncateBeforeLoad: true
            """);

        Assert.Equal(FlowReadModes.Full, profile.ReadMode);
        Assert.True(profile.ReplacesTargetEachRun);
        Assert.Empty(profile.KeyColumns);
        Assert.StartsWith("truncates [dw].[arc].[Dim] first, then inserts every row read", profile.Write, StringComparison.Ordinal);
    }

    [Fact]
    public void FileFlowWithFileDateWatermark_ReadsOnlyNewerFiles()
    {
        var profile = FlowLoadProfiles.FromYaml("""
            name: apc_calls_01_csv
            source:
              type: csv
              location: https://lake.dfs.core.windows.net/raw/apc/Calls/
              options:
                srcFile: "Calls*.csv"
            target:
              connection: ${env:PRE}
              schema: pre
              table: APC_Calls
            incremental:
              dateColumn: FileDate_DW
              overlapDays: 7
            """);

        Assert.Equal(FlowReadModes.Incremental, profile.ReadMode);
        Assert.Equal(["FileDate_DW"], profile.WatermarkColumns);
        Assert.Empty(profile.KeyColumns);
        Assert.Contains("files 'Calls*.csv' under https://lake.dfs.core.windows.net/raw/apc/Calls/ dated after MAX(FileDate_DW)", profile.Read, StringComparison.Ordinal);
        Assert.Contains("minus 7 day(s)", profile.Read, StringComparison.Ordinal);
        Assert.StartsWith("appends the rows read to [pre].[APC_Calls]", profile.Write, StringComparison.Ordinal);
        Assert.False(profile.ReplacesTargetEachRun);
    }

    [Fact]
    public void FileFlowWithoutIncremental_ReadsEveryFile()
    {
        var profile = FlowLoadProfiles.FromYaml("""
            name: ref_01_csv
            source:
              type: csv
              location: /data/ref/
            target:
              connection: ${env:PRE}
              schema: pre
              table: Ref
            load:
              mode: TruncateLoad
            """);

        Assert.Equal(FlowReadModes.Full, profile.ReadMode);
        Assert.True(profile.ReplacesTargetEachRun);
        Assert.Contains("reads every one of the csv files under /data/ref/ on every run", profile.Read, StringComparison.Ordinal);
        Assert.StartsWith("truncates [pre].[Ref], then loads", profile.Write, StringComparison.Ordinal);
    }

    [Fact]
    public void StoredProcedureFlow_IsExternal()
    {
        var profile = FlowLoadProfiles.FromYaml("""
            flowType: sp
            name: build_fact_03_sp
            connections:
              edw: ${env:EDW}
            procedure:
              server: edw
              object: "[dw].[edw].[Build_Fact]"
            """);

        Assert.Equal(FlowReadModes.External, profile.ReadMode);
        Assert.Equal("[dw].[edw].[Build_Fact]", profile.Target);
        Assert.Contains("defined in the procedure body", profile.Read, StringComparison.Ordinal);
    }

    [Fact]
    public void UnparseableOrMissingYaml_ReportsUnknownInsteadOfGuessing()
    {
        var missing = FlowLoadProfiles.FromYaml(null);
        Assert.Equal(FlowReadModes.Unknown, missing.ReadMode);
        Assert.Contains("no YAML", missing.Summary, StringComparison.Ordinal);

        var broken = FlowLoadProfiles.FromYaml("flowType: ing\nname: [unterminated");
        Assert.Equal(FlowReadModes.Unknown, broken.ReadMode);
        Assert.Contains("could not be parsed", broken.Summary, StringComparison.Ordinal);
        Assert.Empty(broken.KeyColumns);
        Assert.Empty(broken.WatermarkColumns);
    }
}
