using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

public sealed class YamlIngestionMatchKeysTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    private static string WithMatchKeys(string matchKeysBlock) => $$"""
        flowType: ing
        name: orders
        connections:
          src: ${env:SRC}
          dwh: ${env:DWH}
        source:
          server: src
          object: db.dbo.Orders
        target:
          server: dwh
          object: dw.raw.Orders
        load:
          keyColumns: [OrderId]
          matchKeysInSourceAndTarget: true
        {{matchKeysBlock}}
        """;

    [Fact]
    public void NoMatchKeysBlock_DefaultsToTagAction_WithDefaults()
    {
        var doc = Loader.Parse(WithMatchKeys(string.Empty));
        var mk = doc.Flow.MatchKeys;

        Assert.Equal(MatchKeyAction.Tag, mk.Action);
        Assert.Equal(20, mk.ActionThresholdPercent);
        Assert.Null(mk.IgnoreDeletedRowsAfterMonths);
        Assert.Null(mk.DateColumn);
        Assert.Empty(mk.KeyColumns);
        Assert.Null(mk.SourceFilter);
        Assert.Null(mk.TargetFilter);
    }

    [Fact]
    public void TagMode_AutoEnablesDeletedDate()
    {
        var doc = Loader.Parse(WithMatchKeys("matchKeys:\n  action: tag"));
        Assert.True(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void TagMode_Default_AutoEnablesDeletedDate()
    {
        var doc = Loader.Parse(WithMatchKeys(string.Empty));
        // load.matchKeysInSourceAndTarget is true and default action is Tag, so DeletedDate auto-enables.
        Assert.True(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void DeleteMode_DoesNotAutoEnableDeletedDate()
    {
        var doc = Loader.Parse(WithMatchKeys("matchKeys:\n  action: delete"));
        Assert.False(doc.Flow.SystemColumns.DeletedDate);
        Assert.Equal(MatchKeyAction.Delete, doc.Flow.MatchKeys.Action);
    }

    [Fact]
    public void MatchKeysNotEnabled_DeletedDateNotAutoEnabled()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
            target:
              server: dwh
              object: dw.raw.Orders
            """;
        var doc = Loader.Parse(yaml);
        Assert.False(doc.Flow.Load.MatchKeysInSourceAndTarget);
        Assert.False(doc.Flow.SystemColumns.DeletedDate);
    }

    [Fact]
    public void FullMatchKeysBlock_MapsAllFields()
    {
        var yaml = WithMatchKeys("""
            matchKeys:
              action: delete
              keyColumns: [OrderId, Region]
              thresholdPercent: 35
              ignoreDeletedRowsAfterMonths: 6
              dateColumn: OrderDate
              sourceFilter: "AND Active = 1"
              targetFilter: "AND Region = 'NA'"
            """);

        var mk = Loader.Parse(yaml).Flow.MatchKeys;

        Assert.Equal(MatchKeyAction.Delete, mk.Action);
        Assert.Equal(["OrderId", "Region"], mk.KeyColumns);
        Assert.Equal(35, mk.ActionThresholdPercent);
        Assert.Equal(6, mk.IgnoreDeletedRowsAfterMonths);
        Assert.Equal("OrderDate", mk.DateColumn);
        Assert.Equal("AND Active = 1", mk.SourceFilter);
        Assert.Equal("AND Region = 'NA'", mk.TargetFilter);
    }

    [Fact]
    public void InvalidAction_Throws()
    {
        var yaml = WithMatchKeys("matchKeys:\n  action: wipe");
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("wipe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThresholdPercent_OutOfRange_Throws()
    {
        var yaml = WithMatchKeys("matchKeys:\n  thresholdPercent: 105");
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("105", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreDeletedRowsAfterMonths_WithoutDateColumn_Throws()
    {
        var yaml = WithMatchKeys("matchKeys:\n  ignoreDeletedRowsAfterMonths: 6");
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("dateColumn", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IgnoreDeletedRowsAfterMonths_WithDateColumn_OK()
    {
        var yaml = WithMatchKeys("""
            matchKeys:
              ignoreDeletedRowsAfterMonths: 6
              dateColumn: OrderDate
            """);
        var mk = Loader.Parse(yaml).Flow.MatchKeys;
        Assert.Equal(6, mk.IgnoreDeletedRowsAfterMonths);
        Assert.Equal("OrderDate", mk.DateColumn);
    }

    [Fact]
    public void TagMode_AutoEnableOverridesExplicitFalseOnDeletedDate()
    {
        // If the user writes systemColumns.deletedDate: false but tag mode is on, the auto-enable wins.
        // This is the legacy mapper behaviour and must hold in YAML too.
        var yaml = WithMatchKeys("""
            matchKeys:
              action: tag
            systemColumns:
              deletedDate: false
            """);
        Assert.True(Loader.Parse(yaml).Flow.SystemColumns.DeletedDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ThresholdPercent_BoundaryValues_Valid(int threshold)
    {
        var yaml = WithMatchKeys($"matchKeys:\n  thresholdPercent: {threshold}");
        Assert.Equal(threshold, Loader.Parse(yaml).Flow.MatchKeys.ActionThresholdPercent);
    }

    [Fact]
    public void ThresholdPercent_Negative_Throws()
    {
        var yaml = WithMatchKeys("matchKeys:\n  thresholdPercent: -1");
        Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
    }

    [Fact]
    public void ActionIsCaseInsensitive()
    {
        foreach (var spelling in new[] { "TAG", "Tag", "tag" })
        {
            var yaml = WithMatchKeys($"matchKeys:\n  action: {spelling}");
            Assert.Equal(MatchKeyAction.Tag, Loader.Parse(yaml).Flow.MatchKeys.Action);
        }

        foreach (var spelling in new[] { "DELETE", "Delete", "delete" })
        {
            var yaml = WithMatchKeys($"matchKeys:\n  action: {spelling}");
            Assert.Equal(MatchKeyAction.Delete, Loader.Parse(yaml).Flow.MatchKeys.Action);
        }
    }
}
