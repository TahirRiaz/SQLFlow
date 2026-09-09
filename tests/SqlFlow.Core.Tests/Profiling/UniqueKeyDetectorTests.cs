using SqlFlow.Core.Profiling;
using Xunit;
using FakeProbe = SqlFlow.Tests.Profiling.FakeUniquenessProbe;

namespace SqlFlow.Tests.Profiling;

/// <summary>
/// The storage-agnostic unique-key detection algorithm, exercised against in-memory tables through
/// <see cref="FakeUniquenessProbe"/>: single-column keys, minimal composite keys (redundancy removed), the declared
/// metadata fast path, the no-key outcome, and the sampling contract that a candidate unique only on the sample is
/// verified against the whole table before it is reported as unique. <see cref="UniqueKeyEdgeCaseTests"/> holds the
/// deeper edge-case matrix.
/// </summary>
public sealed class UniqueKeyDetectorTests
{
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Table(IReadOnlyList<string> columns, params object?[][] rows)
        => FakeUniquenessProbe.Table(columns, rows);

    private static Task<UniqueKeyReport> DetectAsync(FakeProbe probe, IReadOnlyList<string> columns, UniqueKeyOptions? options = null)
        => UniqueKeyDetector.DetectAsync(probe, columns, options ?? new UniqueKeyOptions());

    [Fact]
    public async Task SingleColumnKey_IsDetectedAndPreferred()
    {
        var columns = new[] { "Id", "Name" };
        var rows = Table(columns,
            [1, "a"], [2, "a"], [3, "b"], [4, "b"], [5, "c"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        var top = report.Candidates[0];
        Assert.True(top.IsUnique);
        Assert.True(top.Verified);
        Assert.Equal(new[] { "Id" }, top.Columns);
        // A single-column key exists, so no composite is offered.
        Assert.All(report.Candidates.Where(c => c.IsUnique), c => Assert.Single(c.Columns));
    }

    [Fact]
    public async Task CompositeKey_IsDetectedAndMinimal()
    {
        var columns = new[] { "A", "B", "Const" };
        // (A,B) is unique; neither A nor B alone is; Const is a useless constant.
        var rows = Table(columns,
            [1, "x", 9], [1, "y", 9], [2, "x", 9], [2, "y", 9]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "A", "B" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Const", key.Columns);
    }

    [Fact]
    public async Task DetectedKey_IsMinimal_NoProperSubsetIsUnique()
    {
        // (Y,Z) is the key; X is a decoy the ranking may seed first, so redundancy removal must strip whatever
        // greedy overshoots with. Whatever key is returned, removing any one column must break uniqueness.
        var columns = new[] { "X", "Y", "Z" };
        var rows = Table(columns,
            [1, 1, 1], [2, 1, 2], [3, 2, 1], [4, 2, 2], [5, 3, 1], [1, 3, 2]);
        await using var probe = new FakeProbe(rows, 0);
        var report = await DetectAsync(probe, columns);

        var key = report.Candidates.First(c => c.IsUnique).Columns;
        Assert.True(key.Count >= 2);
        foreach (var column in key)
        {
            var subset = key.Where(c => c != column).ToList();
            var measure = await probe.VerifyAsync(subset);
            Assert.False(measure.IsUnique, $"dropping {column} left a still-unique subset [{string.Join(",", subset)}]; key is not minimal");
        }
    }

    [Fact]
    public async Task NoUniqueKey_ReportsClosestCandidateAndNote()
    {
        var columns = new[] { "A", "B" };
        // Every combination repeats: no key exists at any width.
        var rows = Table(columns,
            [1, "x"], [1, "x"], [2, "y"], [2, "y"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.DoesNotContain(report.Candidates, c => c.IsUnique);
        Assert.NotNull(report.Note);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.All(report.Candidates, c => Assert.True(c.Duplicates > 0));
    }

    [Fact]
    public async Task NullableColumn_IsNeverAKey_EvenWhenDistinct()
    {
        var columns = new[] { "Id", "Opt" };
        // Opt has all-distinct non-null values but one null, so it cannot be a key; Id is the key.
        var rows = Table(columns,
            [1, "p"], [2, "q"], [3, null], [4, "s"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.Equal(new[] { "Id" }, report.Candidates.First(c => c.IsUnique).Columns);
        Assert.DoesNotContain(report.Candidates, c => c.IsUnique && c.Columns.Contains("Opt"));
    }

    [Fact]
    public async Task SampleUniqueButFullDuplicate_IsVerifiedAndRejected()
    {
        var columns = new[] { "Code" };
        // Unique across the first 3 sampled rows, but row 4 duplicates row 1: a real full-table check must reject it.
        var rows = Table(columns, ["a"], ["b"], ["c"], ["a"]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 3), columns);

        Assert.True(report.Sampled);
        var candidate = Assert.Single(report.Candidates);
        Assert.False(candidate.IsUnique);      // rejected by full-table verification
        Assert.True(candidate.Verified);        // and the verdict was measured against the whole table
    }

    [Fact]
    public async Task SampleUniqueAndFullUnique_IsReportedVerified()
    {
        var columns = new[] { "Id" };
        var rows = Table(columns, [1], [2], [3], [4], [5], [6]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 3), columns);

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.IsUnique);
        Assert.True(candidate.Verified);
        Assert.Contains("verified against the whole table", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_BatchesTrials_MeasurementPassesStaySmall()
    {
        // 10 columns, a composite key of two. A naive per-trial probe would scan once per single column and once per
        // trial combination (dozens of passes); the batched search collapses each level into one pass, so the total
        // number of passes is small and, crucially, does not scale with the number of combinations.
        var columns = new[] { "A", "B", "C", "D", "E", "F", "G", "H", "K1", "K2" };
        var rng = new Random(7);
        var rows = Enumerable.Range(0, 40).Select(i =>
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns.Take(8))
            {
                d[c] = rng.Next(0, 3); // low-cardinality noise columns
            }

            d["K1"] = i % 8;   // together K1,K2 uniquely identify the 40 rows
            d["K2"] = i / 8;
            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

        var probe = new FakeProbe(rows, 0);
        var report = await DetectAsync(probe, columns);

        Assert.Contains(report.Candidates, c => c.IsUnique);
        // One pass for all singles, then a bounded handful for the greedy levels and reduction: never the O(combinations)
        // of the naive approach.
        Assert.True(probe.MeasurePasses <= 12, $"expected a small number of batched passes, got {probe.MeasurePasses}");
    }

    [Fact]
    public async Task DeclaredKeys_ShortCircuit_WithoutAnyMeasurement()
    {
        var columns = new[] { "Id", "A", "B" };
        // The rows would NOT support "Id" as a key (a duplicate), proving the declared answer never touches them:
        // a declared key is trusted because the engine enforces it, not because the sample agrees.
        var rows = Table(columns, [1, "x", "y"], [1, "x", "z"]);
        var probe = new FakeProbe(rows, 0) { DeclaredUniqueKeys = [["Id"], ["A", "B"], ["B", "A"]] };
        var report = await DetectAsync(probe, columns);

        Assert.Equal(0, probe.MeasurePasses);
        Assert.Equal(0, report.ScannedRows);
        Assert.Empty(report.Columns);
        Assert.Contains("metadata", report.Note!, StringComparison.OrdinalIgnoreCase);

        // Narrowest first, and the order-insensitive duplicate set (B,A) is collapsed into (A,B).
        Assert.Equal(2, report.Candidates.Count);
        Assert.Equal(new[] { "Id" }, report.Candidates[0].Columns);
        Assert.Equal(new[] { "A", "B" }, report.Candidates[1].Columns);
        Assert.All(report.Candidates, c =>
        {
            Assert.True(c.IsUnique);
            Assert.True(c.Verified);
            Assert.True(c.Declared);
        });
    }

    [Fact]
    public async Task DeclaredKeys_RespectMaxCandidates()
    {
        var columns = new[] { "Id", "Alt" };
        var rows = Table(columns, [1, 10], [2, 20]);
        var probe = new FakeProbe(rows, 0) { DeclaredUniqueKeys = [["Id"], ["Alt"]] };
        var report = await DetectAsync(probe, columns, new UniqueKeyOptions { MaxCandidates = 1 });

        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(new[] { "Alt" }, candidate.Columns); // ties on width break alphabetically
    }

    [Fact]
    public async Task EmptyTable_YieldsNoCandidatesWithNote()
    {
        var columns = new[] { "Id" };
        var report = await DetectAsync(new FakeProbe(Table(columns), 0), columns);

        Assert.Empty(report.Candidates);
        Assert.Equal(0, report.TotalRows);
        Assert.Contains("empty", report.Note!, StringComparison.OrdinalIgnoreCase);
    }
}
