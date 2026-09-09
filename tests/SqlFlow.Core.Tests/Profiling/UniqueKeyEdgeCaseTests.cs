using SqlFlow.Core.Profiling;
using Xunit;
using FakeProbe = SqlFlow.Tests.Profiling.FakeUniquenessProbe;

namespace SqlFlow.Tests.Profiling;

/// <summary>
/// The edge-case matrix for the storage-agnostic detector, grouped by the dimension being stressed: degenerate
/// table shapes (one row, all-null and constant columns, fully duplicated data), the search limits (key width cap,
/// scan-pool cap, the distinct-product impossibility bound), the sampling and verification contract (unverified
/// estimates, rejection by nulls the sample never saw), and the declared-key fast path (empty sets, case-insensitive
/// duplicates, empty tables). Complements <see cref="UniqueKeyDetectorTests"/>, which proves the happy paths.
/// </summary>
public sealed class UniqueKeyEdgeCaseTests
{
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Table(IReadOnlyList<string> columns, params object?[][] rows)
        => FakeUniquenessProbe.Table(columns, rows);

    private static Task<UniqueKeyReport> DetectAsync(FakeProbe probe, IReadOnlyList<string> columns, UniqueKeyOptions? options = null)
        => UniqueKeyDetector.DetectAsync(probe, columns, options ?? new UniqueKeyOptions());

    // ---- Degenerate table shapes -------------------------------------------------------------------------------

    [Fact]
    public async Task SingleRowTable_EveryNonNullColumnIsAKey_AndNullColumnIsNot()
    {
        var columns = new[] { "Id", "Name", "Opt" };
        var rows = Table(columns, [1, "x", null]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        // With one row, every non-null column is trivially unique; the null column can never be a key.
        Assert.Contains(report.Candidates, c => c.IsUnique && c.Columns.SequenceEqual(new[] { "Id" }));
        Assert.Contains(report.Candidates, c => c.IsUnique && c.Columns.SequenceEqual(new[] { "Name" }));
        Assert.DoesNotContain(report.Candidates, c => c.Columns.Contains("Opt"));
    }

    [Fact]
    public async Task AllNullColumn_IsNeverConsidered_KeyFoundElsewhere()
    {
        var columns = new[] { "Id", "Dead" };
        var rows = Table(columns, [1, null], [2, null], [3, null]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.Equal(new[] { "Id" }, key.Columns);
        Assert.DoesNotContain(report.Candidates, c => c.Columns.Contains("Dead"));
        // The stats still report the column so the operator sees why it was useless.
        Assert.Equal(3, report.Columns.Single(s => s.Column == "Dead").Nulls);
    }

    [Fact]
    public async Task AllColumnsConstant_NoSearchPool_NoKeyWithNote()
    {
        var columns = new[] { "A", "B", "C" };
        var rows = Table(columns, [1, "k", 9], [1, "k", 9], [1, "k", 9], [1, "k", 9]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        // Constants cannot enter the pool, so there is not even a near-miss combination to show.
        Assert.Empty(report.Candidates);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullyDuplicatedTable_NoKeyAtAnyWidth_NearMissCarriesDuplicateCount()
    {
        var columns = new[] { "A", "B", "C" };
        // Every row appears twice: even the full tuple cannot be unique.
        var rows = Table(columns,
            [1, "x", 10], [1, "x", 10], [2, "y", 20], [2, "y", 20], [3, "z", 30], [3, "z", 30]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.DoesNotContain(report.Candidates, c => c.IsUnique);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
        var nearMiss = report.Candidates[0];
        Assert.Equal(3, nearMiss.Duplicates); // 6 rows, 3 distinct tuples: 3 rows collide
    }

    [Fact]
    public async Task EmptyColumnList_OnNonEmptyRows_ReportsNothingToAnalyze()
    {
        var rows = Table(["Id"], [1], [2]);
        var report = await DetectAsync(new FakeProbe(rows, 0), []);

        Assert.Empty(report.Candidates);
        Assert.Contains("no columns", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Search limits -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ProductBound_RulesOutImpossibleTables_WithoutClaimingAKey()
    {
        var columns = new[] { "P", "Q", "R" };
        // 16 rows but each column has only 2 distinct values: the product bound (2*2*2 = 8 < 16) proves no subset
        // can be unique, and the report still shows the closest combination so the gap is visible.
        var rows = Enumerable.Range(0, 16)
            .Select(i => new object?[] { i % 2, (i / 2) % 2, (i / 4) % 2 })
            .ToArray();
        var report = await DetectAsync(new FakeProbe(Table(columns, rows), 0), columns);

        Assert.DoesNotContain(report.Candidates, c => c.IsUnique);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(report.Candidates);
        Assert.True(report.Candidates[0].Duplicates > 0);
    }

    [Fact]
    public async Task MaxKeyColumns_CapsTheSearch_RaisingItFindsTheWiderKey()
    {
        var columns = new[] { "P1", "P2", "P3" };
        // The only key is all three columns together (i decomposed into three binary digits over 8 rows).
        var rows = Enumerable.Range(0, 8)
            .Select(i => new object?[] { i % 2, (i / 2) % 2, i / 4 })
            .ToArray();
        var data = Table(columns, rows);

        var capped = await DetectAsync(new FakeProbe(data, 0), columns, new UniqueKeyOptions { MaxKeyColumns = 2 });
        Assert.DoesNotContain(capped.Candidates, c => c.IsUnique);
        Assert.Contains("max-columns", capped.Note!, StringComparison.OrdinalIgnoreCase);

        var raised = await DetectAsync(new FakeProbe(data, 0), columns, new UniqueKeyOptions { MaxKeyColumns = 3 });
        var key = Assert.Single(raised.Candidates, c => c.IsUnique);
        Assert.Equal(3, key.Columns.Count);
    }

    [Fact]
    public async Task MaxScanColumns_LimitsThePool_RaisingItReachesLowerRankedKeyColumns()
    {
        // 12 identical high-cardinality noise columns outrank the two low-cardinality columns that actually form
        // the key, so with the default pool cap of 12 the key columns never enter the search. Raising the cap
        // admits them and a key is found.
        var noise = Enumerable.Range(0, 12).Select(j => $"N{j:00}").ToArray();
        var columns = noise.Append("A").Append("B").ToArray();
        var rows = Enumerable.Range(0, 16)
            .Select(i => noise.Select(_ => (object?)(i / 2)).Append(i % 4).Append(i / 4).ToArray())
            .ToArray();
        var data = Table(columns, rows);

        var capped = await DetectAsync(new FakeProbe(data, 0), columns, new UniqueKeyOptions { MaxScanColumns = 12 });
        Assert.DoesNotContain(capped.Candidates, c => c.IsUnique);

        var raised = await DetectAsync(new FakeProbe(data, 0), columns, new UniqueKeyOptions { MaxScanColumns = 14 });
        Assert.Contains(raised.Candidates, c => c.IsUnique);
    }

    [Fact]
    public async Task MaxCandidates_CapsTheReport_UniqueKeysSurviveTheCut()
    {
        var columns = new[] { "K1", "K2", "K3" };
        // Three independent single-column keys; a cap of 2 must keep two unique ones, never a weaker candidate.
        var rows = Table(columns, [1, 10, "a"], [2, 20, "b"], [3, 30, "c"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns, new UniqueKeyOptions { MaxCandidates = 2 });

        Assert.Equal(2, report.Candidates.Count);
        Assert.All(report.Candidates, c => Assert.True(c.IsUnique));
    }

    [Fact]
    public async Task GreedyStall_NoExtensionImproves_ReportsNearMissInsteadOfLooping()
    {
        var columns = new[] { "X", "Y" };
        // X and Y are perfectly correlated (Y = X), so extending {X} with Y adds nothing: the greedy build must
        // stop on "no improvement", not spin until the width cap.
        var rows = Table(columns, [1, 1], [1, 1], [2, 2], [2, 2]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.DoesNotContain(report.Candidates, c => c.IsUnique);
        Assert.NotEmpty(report.Candidates);
        Assert.True(report.Candidates[0].Duplicates > 0);
    }

    // ---- Sampling and verification contract --------------------------------------------------------------------

    [Fact]
    public async Task NoVerify_SampledCandidate_IsMarkedUnverifiedEstimate()
    {
        var columns = new[] { "Code" };
        var rows = Table(columns, ["a"], ["b"], ["c"], ["d"], ["e"], ["f"]);
        var report = await DetectAsync(
            new FakeProbe(rows, sampleSize: 3), columns, new UniqueKeyOptions { Verify = false });

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.IsUnique);
        Assert.False(candidate.Verified);
        Assert.True(candidate.Estimated);
        Assert.Contains("NOT verified", report.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_RejectsCandidate_WhenNullsExistOnlyOutsideTheSample()
    {
        var columns = new[] { "Code" };
        // Unique and non-null across the sampled prefix; the null in row 4 is only visible to the full-table
        // verdict, which must reject the candidate even though no duplicate exists.
        var rows = Table(columns, ["a"], ["b"], ["c"], [null]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 3), columns);

        var candidate = Assert.Single(report.Candidates);
        Assert.False(candidate.IsUnique);
        Assert.True(candidate.Verified);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SampledComposite_IsVerifiedAgainstTheWholeTable_BeforeBeingReported()
    {
        var columns = new[] { "G", "S" };
        // (G,S) is unique on the 4-row sample AND on the full table; the report must mark it verified, proving the
        // whole-table check ran for a composite (not just for single columns).
        var rows = Table(columns,
            [1, 1], [1, 2], [2, 1], [2, 2], [3, 1], [3, 2], [4, 1], [4, 2]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 4), columns);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(2, key.Columns.Count);
    }

    // ---- Declared-key fast path --------------------------------------------------------------------------------

    [Fact]
    public async Task DeclaredKey_WithEmptyColumnSet_IsIgnored_ProfilingProceeds()
    {
        var columns = new[] { "Id" };
        var rows = Table(columns, [1], [2], [3]);
        var probe = new FakeProbe(rows, 0) { DeclaredUniqueKeys = [[]] };
        var report = await DetectAsync(probe, columns);

        // The degenerate declared entry must not short-circuit: the real key comes from measurement.
        Assert.True(probe.MeasurePasses > 0);
        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.False(key.Declared);
        Assert.Equal(new[] { "Id" }, key.Columns);
    }

    [Fact]
    public async Task DeclaredKeys_DifferingOnlyByCase_CollapseToOneCandidate()
    {
        var columns = new[] { "Id" };
        var rows = Table(columns, [1], [2]);
        var probe = new FakeProbe(rows, 0) { DeclaredUniqueKeys = [["ID"], ["id"]] };
        var report = await DetectAsync(probe, columns);

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.Declared);
    }

    [Fact]
    public async Task DeclaredKey_OnEmptyTable_IsStillReported()
    {
        var columns = new[] { "Id" };
        var probe = new FakeProbe(Table(columns), 0) { DeclaredUniqueKeys = [["Id"]] };
        var report = await DetectAsync(probe, columns);

        // An empty table has no rows to contradict the constraint the engine enforces; the declared answer stands
        // (the plain empty-table note is for the profiling path, which never runs here).
        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.IsUnique);
        Assert.True(candidate.Declared);
        Assert.Equal(0, report.TotalRows);
    }

    [Fact]
    public async Task DeclaredKey_WithEmptyColumnList_StillShortCircuits()
    {
        // Even when the caller has no searchable columns to offer (all were pruned), a declared key answers.
        var probe = new FakeProbe(Table(["Id"], [1], [2]), 0) { DeclaredUniqueKeys = [["Id"]] };
        var report = await UniqueKeyDetector.DetectAsync(probe, [], new UniqueKeyOptions());

        Assert.Equal(0, probe.MeasurePasses);
        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.Declared);
    }
}
