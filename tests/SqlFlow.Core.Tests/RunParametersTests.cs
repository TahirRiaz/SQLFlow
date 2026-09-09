using System;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The per-run substitution parameters contract (the built-in backfill): the validation matrix at the trust
/// boundary, the default detection that keeps an unparameterized run untouched, and the human description. These
/// run everywhere (pure, no database), so the validation the API and CLI both depend on is always exercised.
/// </summary>
public sealed class RunParametersTests
{
    [Theory]
    [InlineData("AND pk > 92992")]
    [InlineData("and Dat >= '2026-08-05'")]
    [InlineData("  OR Status = 'X'  ")]
    public void SourceFilter_AcceptsAPredicateContinuation(string filter)
    {
        var parameters = new RunParameters { SourceFilter = filter };
        parameters.Validate(); // never throws
        Assert.False(parameters.IsDefault);
        Assert.Contains(filter.Trim(), parameters.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pk > 92992")]                      // not a continuation: no leading connector
    [InlineData("AND pk > 1; DROP TABLE x")]        // statement terminator
    [InlineData("AND pk > 1 -- rest")]              // line comment
    [InlineData("AND pk > 1 /* block */")]          // block comment
    [InlineData("   ")]                             // blank
    public void SourceFilter_RejectsAnythingThatIsNotAPlainPredicate(string filter)
        => Assert.Throws<SqlFlowException>(() => new RunParameters { SourceFilter = filter }.Validate());

    [Fact]
    public void SourceFilter_RejectsOverlongValue()
        => Assert.Throws<SqlFlowException>(() =>
            new RunParameters { SourceFilter = "AND x = " + new string('9', RunParameters.MaxSourceFilterLength) }.Validate());

    [Fact]
    public void SourceFilter_CannotBeCombinedWithAssertionsOnly()
        => Assert.Throws<SqlFlowException>(() =>
            new RunParameters { AssertionsOnly = true, SourceFilter = "AND pk > 1" }.Validate());

    [Fact]
    public void SourceFilter_ComposesWithAFullLoadAndAWindow()
    {
        // Neither combination is contradictory: full load drops the watermark, the window and the filter narrow
        // the read. Validate must not reject what the resolver honors.
        new RunParameters { FullLoad = true, SourceFilter = "AND pk > 1" }.Validate();
        new RunParameters
        {
            BackfillFrom = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
            BackfillTo = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc),
            SourceFilter = "AND pk > 1",
        }.Validate();
    }

    [Fact]
    public void None_IsDefault_AndDescribesAsNone()
    {
        Assert.True(RunParameters.None.IsDefault);
        Assert.Equal("none", RunParameters.None.Describe());
        RunParameters.None.Validate(); // never throws
    }

    [Theory]
    [InlineData(true, false, false, false)]  // full load only
    [InlineData(false, true, false, false)]  // from only
    [InlineData(false, true, true, false)]   // full window
    [InlineData(false, false, false, true)]  // file pattern only
    public void IsDefault_IsFalse_WhenAnyParameterIsSet(bool full, bool from, bool to, bool pattern)
    {
        var parameters = new RunParameters
        {
            FullLoad = full,
            BackfillFrom = from ? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            BackfillTo = to ? new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            FilePattern = pattern ? "orders*.csv" : null,
        };
        Assert.False(parameters.IsDefault);
    }

    [Fact]
    public void FullLoadWithAWindow_IsRejected()
    {
        var parameters = new RunParameters
        {
            FullLoad = true,
            BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("mutually exclusive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedWindow_IsRejected()
    {
        var parameters = new RunParameters
        {
            BackfillFrom = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            BackfillTo = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void EqualWindowBounds_AreRejected()
    {
        var instant = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var parameters = new RunParameters { BackfillFrom = instant, BackfillTo = instant };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void UpperBoundWithoutLowerBound_IsRejected()
    {
        var parameters = new RunParameters { BackfillTo = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("requires backfillFrom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenEndedWindow_FromWithNoTo_IsValid()
    {
        var parameters = new RunParameters { BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        parameters.Validate(); // a lower bound with no upper bound is a legitimate "everything since" backfill
        Assert.Contains("from 2023-01-01", parameters.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFilePattern_IsRejected(string pattern)
    {
        Assert.Throws<SqlFlowException>(new RunParameters { FilePattern = pattern }.Validate);
    }

    [Fact]
    public void OversizedFilePattern_IsRejected()
    {
        var parameters = new RunParameters { FilePattern = new string('a', RunParameters.MaxFilePatternLength + 1) };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void ControlCharacterInFilePattern_IsRejected()
    {
        Assert.Throws<SqlFlowException>(new RunParameters { FilePattern = "orders\t.csv" }.Validate);
    }

    [Fact]
    public void MaxLengthFilePattern_IsAccepted()
    {
        new RunParameters { FilePattern = new string('a', RunParameters.MaxFilePatternLength) }.Validate();
    }

    [Fact]
    public void AssertionsOnly_IsNotDefault_AndDescribes()
    {
        var parameters = new RunParameters { AssertionsOnly = true };
        parameters.Validate();
        Assert.False(parameters.IsDefault);
        Assert.Equal("assertions only", parameters.Describe());
    }

    [Theory]
    [InlineData(true, false, false)]  // with full load
    [InlineData(false, true, false)]  // with a window bound
    [InlineData(false, false, true)]  // with a file pattern
    public void AssertionsOnly_CombinedWithAnySelectionOverride_IsRejected(bool full, bool from, bool pattern)
    {
        var parameters = new RunParameters
        {
            AssertionsOnly = true,
            FullLoad = full,
            BackfillFrom = from ? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            FilePattern = pattern ? "orders*.csv" : null,
        };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("assertionsOnly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_RendersEveryComponent()
    {
        var parameters = new RunParameters
        {
            BackfillFrom = new DateTime(2023, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            BackfillTo = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            FilePattern = "orders*.csv",
        };
        var description = parameters.Describe();
        Assert.Contains("window 2023-01-01 06:00:00 .. 2023-02-01 00:00:00", description, StringComparison.Ordinal);
        Assert.Contains("files 'orders*.csv'", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ReprocessFromSourceMin_IsNotDefault_AndDescribes()
    {
        var parameters = new RunParameters { ReprocessFromSourceMin = true };
        parameters.Validate(); // a descendant's reprocess flag on its own is a legitimate run
        Assert.False(parameters.IsDefault);
        Assert.Equal("reprocess from source min", parameters.Describe());
    }

    [Theory]
    [InlineData(true, false, false)]  // with full load
    [InlineData(false, true, false)]  // with a window bound
    [InlineData(false, false, true)]  // with assertions-only
    public void ReprocessFromSourceMin_CombinedWithAnotherOverride_IsRejected(bool full, bool from, bool assertions)
    {
        var parameters = new RunParameters
        {
            ReprocessFromSourceMin = true,
            FullLoad = full,
            BackfillFrom = from ? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            AssertionsOnly = assertions,
        };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("reprocessFromSourceMin", error.Message, StringComparison.Ordinal);
    }
}
