using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The flowType: ing versioning.scd2 surface: it maps to the policy with sensible defaults, and the guards
/// reject the combinations that cannot be correct (no key, a truncate that would erase history, both SCD2 and
/// system-versioned temporal, colliding period column names).
/// </summary>
public sealed class Scd2LoaderTests
{
    private static YamlIngestionFlowLoader Loader() => new();

    private static string Doc(string versioning, string load = "  keyColumns: [CustomerId]", string target = "") => $"""
        flowType: ing
        name: dim_customer
        connections:
          SRC:
          DW:
        source:
          server: SRC
          object: Src.dbo.Customer
        target:
          server: DW
          object: DW.dim.Customer
        {target}
        load:
        {load}
        {versioning}
        """;

    [Fact]
    public void MapsScd2_WithDefaultsAndOverrides()
    {
        var doc = Loader().Parse(Doc("""
            versioning:
              scd2:
                enabled: true
                trackedColumns: [City, Segment]
            """));
        var scd2 = doc.Flow.Versioning.Scd2;

        Assert.True(scd2.Enabled);
        Assert.Equal("ValidFrom_DW", scd2.ValidFromColumn);
        Assert.Equal("ValidTo_DW", scd2.ValidToColumn);
        Assert.Equal("IsCurrent_DW", scd2.CurrentFlagColumn);
        Assert.Equal(["City", "Segment"], scd2.TrackedColumns);
    }

    [Fact]
    public void HonorsCustomColumnNames()
    {
        var doc = Loader().Parse(Doc("""
            versioning:
              scd2:
                enabled: true
                validFromColumn: EffectiveFrom
                validToColumn: EffectiveTo
                currentFlagColumn: IsActive
            """));
        var scd2 = doc.Flow.Versioning.Scd2;

        Assert.Equal("EffectiveFrom", scd2.ValidFromColumn);
        Assert.Equal("EffectiveTo", scd2.ValidToColumn);
        Assert.Equal("IsActive", scd2.CurrentFlagColumn);
    }

    [Fact]
    public void Rejects_Scd2WithoutKeyColumns()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(Doc("""
            versioning:
              scd2:
                enabled: true
            """, load: "  threads: 1")));
        Assert.Contains("keyColumns", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_Scd2WithTruncate()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: ing
            name: dim_customer
            connections:
              SRC:
              DW:
            source:
              server: SRC
              object: Src.dbo.Customer
            target:
              server: DW
              object: DW.dim.Customer
              truncateBeforeLoad: true
            load:
              keyColumns: [CustomerId]
            versioning:
              scd2:
                enabled: true
            """));
        Assert.Contains("truncate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two history mechanisms on one table would record every change twice: SQL Server's own row
    /// versions AND an engine-maintained period row.</summary>
    [Fact]
    public void Rejects_Scd2WithTemporalHistory()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(Doc("""
            versioning:
              temporalHistory: true
              scd2:
                enabled: true
            """)));
        Assert.Contains("versioning.temporal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("versioning.scd2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_CollidingPeriodColumnNames()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(Doc("""
            versioning:
              scd2:
                enabled: true
                validFromColumn: Period
                validToColumn: Period
            """)));
        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
