using System.Linq;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// The per-kind applicability rules the trigger UI renders from: they must mirror what the engine actually honors,
/// so a user is never offered a control the run would ignore or reject.
/// </summary>
public sealed class RunParameterApplicabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ingestion_AlwaysOffersTheSourceFilter_WhateverItDeclares(bool hasDateColumn)
    {
        // The window needs a declared date column; the raw filter does not. That is the whole point of it: a flow
        // with no usable date column must still be backfillable without a YAML edit.
        var keys = RunParameterApplicability.For("ing", hasDateColumn).Select(p => p.Key).ToList();
        Assert.Contains("sourceFilter", keys);
        Assert.Equal(hasDateColumn, keys.Contains("backfillWindow"));
    }

    [Fact]
    public void SourceFilter_IsOfferedOnlyToRelationalIngestion()
    {
        foreach (var kind in new[] { "cpy", "file", "exp", "api", "sftp" })
        {
            Assert.DoesNotContain(
                "sourceFilter",
                RunParameterApplicability.For(kind, hasIncrementalDateColumn: true).Select(p => p.Key));
        }
    }

    [Fact]
    public void Copy_OffersFullLoadAndBackfillWindow()
    {
        var keys = RunParameterApplicability.For("cpy", hasIncrementalDateColumn: false).Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "fullLoad", "backfillWindow" }, keys);
    }

    [Fact]
    public void File_OffersFullLoadWindowAndPattern()
    {
        var keys = RunParameterApplicability.For("file", hasIncrementalDateColumn: false).Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "fullLoad", "backfillWindow", "filePattern" }, keys);
    }

    [Fact]
    public void Ingestion_OffersBackfillWindowOnlyWhenItHasADateColumn()
    {
        Assert.DoesNotContain("backfillWindow", RunParameterApplicability.For("ing", false).Select(p => p.Key));
        var withColumn = RunParameterApplicability.For("ing", true).Select(p => p.Key).ToList();
        Assert.Contains("backfillWindow", withColumn);
        Assert.Contains("assertionsOnly", withColumn);
        Assert.Contains("fullLoad", withColumn);
    }

    [Theory]
    [InlineData("sp")]
    [InlineData("hc")]
    [InlineData("inv")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void KindsWithoutSelectionSurface_OfferNothing(string? kind)
        => Assert.Empty(RunParameterApplicability.For(kind, hasIncrementalDateColumn: true));

    [Fact]
    public void KindMatch_IsCaseInsensitive()
        => Assert.NotEmpty(RunParameterApplicability.For("CPY", hasIncrementalDateColumn: false));

    [Fact]
    public void EveryDescriptor_CarriesLabelAndHelp()
    {
        foreach (var kind in new[] { "cpy", "file", "ing", "exp" })
        {
            foreach (var descriptor in RunParameterApplicability.For(kind, hasIncrementalDateColumn: true))
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Label));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Help));
            }
        }
    }
}
