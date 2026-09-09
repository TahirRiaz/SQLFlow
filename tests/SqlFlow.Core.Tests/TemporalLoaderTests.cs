using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The flowType: ing versioning.temporal surface. Two authoring shapes converge on one policy: the legacy
/// <c>temporalHistory: true</c> shorthand (what a ported trgVersioning flow carries) and the full
/// <c>temporal:</c> block. The guards reject what SQL Server cannot do (a truncate on a versioned table, a
/// cross-database history name) and what the author cannot have meant (a shorthand contradicting the block,
/// two history mechanisms at once, one column serving as both ends of the period).
/// </summary>
public sealed class TemporalLoaderTests
{
    private static YamlIngestionFlowLoader Loader() => new();

    private static string Doc(string versioning, string target = "") => $"""
        flowType: ing
        name: arc_bilteller
        connections:
          SRC:
          DW:
        source:
          server: SRC
          object: Pre.pre.v_SVV_Bilteller
        target:
          server: DW
          object: DW.arc.SVV_Bilteller
          identityColumn: SVVBiltellerPK
        {target}
        load:
          keyColumns: [Trafikkregistreringspunkt]
        {versioning}
        """;

    private static TemporalPolicy Parse(string versioning, string target = "")
        => Loader().Parse(Doc(versioning, target)).Flow.Versioning.Temporal;

    [Fact]
    public void AbsentVersioning_LeavesTemporalOff()
    {
        var temporal = Parse(string.Empty);

        Assert.False(temporal.Enabled);
        Assert.Equal("ver", temporal.HistorySchema);
        Assert.Equal(7, temporal.PeriodPrecision);
    }

    /// <summary>The legacy shorthand is the whole surface a ported trgVersioning flow needs.</summary>
    [Fact]
    public void TemporalHistoryShorthand_EnablesTheFeatureWithLegacyDefaults()
    {
        var temporal = Parse("""
            versioning:
              temporalHistory: true
            """);

        Assert.True(temporal.Enabled);
        Assert.Equal("ver", temporal.HistorySchema);
        Assert.Null(temporal.HistoryTable);
        Assert.Equal("ValidFrom_DW", temporal.ValidFromColumn);
        Assert.Equal("ValidTo_DW", temporal.ValidToColumn);
        Assert.True(temporal.HiddenPeriodColumns);
        Assert.Null(temporal.RetentionDays);
    }

    [Fact]
    public void TemporalBlock_MapsEveryKnob()
    {
        var temporal = Parse("""
            versioning:
              temporal:
                enabled: true
                historySchema: history
                historyTable: SVV_Bilteller_Versions
                validFromColumn: SysStart
                validToColumn: SysEnd
                hiddenPeriodColumns: false
                periodPrecision: 0
                retentionDays: 3650
            """);

        Assert.True(temporal.Enabled);
        Assert.Equal("history", temporal.HistorySchema);
        Assert.Equal("SVV_Bilteller_Versions", temporal.HistoryTable);
        Assert.Equal("SysStart", temporal.ValidFromColumn);
        Assert.Equal("SysEnd", temporal.ValidToColumn);
        Assert.False(temporal.HiddenPeriodColumns);
        Assert.Equal(0, temporal.PeriodPrecision);
        Assert.Equal(3650, temporal.RetentionDays);
    }

    /// <summary>The shorthand and the block are one setting, so the shorthand can enable a block that only
    /// carries configuration.</summary>
    [Fact]
    public void ShorthandEnables_ABlockThatOnlyConfigures()
    {
        var temporal = Parse("""
            versioning:
              temporalHistory: true
              temporal:
                historySchema: history
            """);

        Assert.True(temporal.Enabled);
        Assert.Equal("history", temporal.HistorySchema);
    }

    [Fact]
    public void ResolveHistoryTable_DefaultsToTheTargetName()
    {
        Assert.Equal("SVV_Bilteller", new TemporalPolicy().ResolveHistoryTable("SVV_Bilteller"));
        Assert.Equal("Custom", new TemporalPolicy { HistoryTable = " Custom " }.ResolveHistoryTable("SVV_Bilteller"));
    }

    // ---------------------------------------------------------------- guards

    [Fact]
    public void Rejects_ShorthandContradictingTheBlock()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            versioning:
              temporalHistory: false
              temporal:
                enabled: true
            """));

        Assert.Contains("contradicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SQL Server does not allow TRUNCATE TABLE on a system-versioned table. Legacy skipped the
    /// truncate silently; rejecting the pair is the honest behavior.</summary>
    [Fact]
    public void Rejects_TruncateBeforeLoad()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse(
            """
            versioning:
              temporalHistory: true
            """,
            target: "  truncateBeforeLoad: true"));

        Assert.Contains("truncateBeforeLoad", ex.Message, StringComparison.Ordinal);
        Assert.Contains("system-versioned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_TemporalTogetherWithScd2()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            versioning:
              temporal:
                enabled: true
              scd2:
                enabled: true
            """));

        Assert.Contains("versioning.temporal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("versioning.scd2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_IdenticalPeriodColumnNames()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            versioning:
              temporal:
                enabled: true
                validFromColumn: Period
                validToColumn: Period
            """));

        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The history table must live in the target's own database: SQL Server accepts only a two-part
    /// history name, so a dotted one is a misunderstanding worth catching at authoring time.</summary>
    [Theory]
    [InlineData("historySchema: other_db.ver")]
    [InlineData("historyTable: ver.Bilteller")]
    public void Rejects_DottedHistoryNames(string setting)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse($"""
            versioning:
              temporal:
                enabled: true
                {setting}
            """));

        Assert.Contains("same database", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void Rejects_PeriodPrecisionOutOfRange(int precision)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse($"""
            versioning:
              temporal:
                enabled: true
                periodPrecision: {precision}
            """));

        Assert.Contains("periodPrecision", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Rejects_NonPositiveRetention(int days)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse($"""
            versioning:
              temporal:
                enabled: true
                retentionDays: {days}
            """));

        Assert.Contains("retentionDays", ex.Message, StringComparison.Ordinal);
    }
}
