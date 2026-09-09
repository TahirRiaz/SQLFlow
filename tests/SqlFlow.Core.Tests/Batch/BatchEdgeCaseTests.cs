using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Secrets;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Batch;

/// <summary>
/// Additional, non-overlapping edge cases for the batch feature: glob selection (filename, subdirectory, exclude
/// precedence, union of patterns, case-insensitivity), wave shape over linear, forked, and independent estates,
/// the failure semantics (stop in a later wave, continue's transitive skip across three levels, ignoreErrors
/// under both modes), the inactive set (all-inactive, an inactive producer), cycle handling (a two-flow mutual
/// pair is broken while a ring survives and coexists with an ordered chain), concurrency bounds (maxParallel of
/// one, two, the machine-sized default, and larger than the wave), a runner that throws rather than returns, cross-server objects
/// that do not link, duplicate member names, cancellation, and the aggregate counts and echoed fields. Every
/// member runs through a faked <see cref="IDocumentRunner"/>, so the orchestration is proven without SQL; the
/// loader cases parse YAML in memory. All inputs are fixed, so the tests are deterministic. Helper names are
/// prefixed so they cannot collide with other files in the namespace.
/// </summary>
public sealed class BatchEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_batch_edge_" + Guid.NewGuid().ToString("N"));

    public BatchEdgeCaseTests() => Directory.CreateDirectory(_dir);

    // ---- estate authoring (uniquely named helpers) --------------------------------------------------------

    /// <summary>Writes one ingestion flow: it reads sourceServer/sourceObject and writes targetServer/targetObject.
    /// A dependency forms only when one flow's written object equals another flow's read object on the SAME
    /// connection (a bare alias resolves the conventional ${env:SQLFLOW_CONN_&lt;alias&gt;} reference, so the
    /// alias name IS the server identity).</summary>
    private void WriteIngFlow(string name, string sourceServer, string sourceObject, string targetServer, string targetObject, string? subdirectory = null)
    {
        var connections = sourceServer == targetServer ? $"  {sourceServer}:\n" : $"  {sourceServer}:\n  {targetServer}:\n";
        var yaml = $"""
            flowType: ing
            name: {name}
            connections:
            {connections}source:
              server: {sourceServer}
              object: {sourceObject}
            target:
              server: {targetServer}
              object: {targetObject}
            """;
        var folder = subdirectory is null ? _dir : Path.Combine(_dir, subdirectory);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".flow.yaml"), yaml);
    }

    /// <summary>A self-contained, independent ingestion flow whose objects touch nothing else, so it shares wave 1
    /// with every other independent flow and depends on nothing.</summary>
    private void WriteIndependentFlow(string name)
        => WriteIngFlow(name, "ISO_" + name, $"ISO_{name}.s.In", "ISO_" + name, $"ISO_{name}.s.Out");

    // ---- run plumbing -------------------------------------------------------------------------------------

    private static BatchFlow MakeBatch(
        BatchErrorMode onError = BatchErrorMode.Stop,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        IReadOnlyList<string>? inactive = null,
        IReadOnlyList<string>? ignoreErrors = null,
        int maxParallel = 0)
        => new()
        {
            FlowId = 1,
            SysAlias = "batch-edge",
            Include = include ?? ["**/*.flow.yaml"],
            Exclude = exclude ?? [],
            Inactive = inactive ?? [],
            IgnoreErrors = ignoreErrors ?? [],
            OnError = onError,
            MaxParallel = maxParallel,
            Connect = BatchConnectMode.Never,
        };

    private Task<BatchRunResult> RunAsync(BatchFlow flow, IDocumentRunner runner, CancellationToken ct = default)
        => new BatchOrchestrator(runner).RunAsync(
            flow,
            Path.Combine(_dir, "edge.batch.yaml"),
            new SecretResolver([new EnvSecretProvider()]),
            new DocumentExecutionOptions(),
            ct);

    private static BatchMemberResult Member(BatchRunResult result, string name)
        => result.Members.Single(m => string.Equals(m.FlowName, name, StringComparison.OrdinalIgnoreCase));

    // ---- glob selection -----------------------------------------------------------------------------------

    [Fact]
    public async Task IncludeByExactFileName_SelectsOnlyThatMember()
    {
        WriteIndependentFlow("alpha");
        WriteIndependentFlow("beta");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["alpha.flow.yaml"]), runner);

        Assert.True(result.Success);
        Assert.Single(result.Members);
        Assert.Equal("alpha", Member(result, "alpha").FlowName);
        Assert.False(runner.WasRun("beta"));
    }

    [Fact]
    public async Task IncludeByFileNamePrefixGlob_SelectsOnlyMatchingMembers()
    {
        WriteIngFlow("raw_one", "S", "S.s.One", "S", "S.s.One2");
        WriteIngFlow("raw_two", "S", "S.s.Two", "S", "S.s.Two2");
        WriteIndependentFlow("dim_other");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["raw_*.flow.yaml"]), runner);

        Assert.Equal(2, result.Members.Count);
        Assert.True(runner.WasRun("raw_one"));
        Assert.True(runner.WasRun("raw_two"));
        Assert.False(runner.WasRun("dim_other"));
    }

    [Fact]
    public async Task IncludeBySubdirectoryGlob_SelectsOnlyNestedMembers()
    {
        WriteIngFlow("nested", "S", "S.s.In", "S", "S.s.Out", subdirectory: "raw");
        WriteIndependentFlow("flat");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["raw/*.flow.yaml"]), runner);

        Assert.Single(result.Members);
        // The reported file path is repository-relative with forward slashes.
        Assert.Equal("raw/nested.flow.yaml", Member(result, "nested").File);
        Assert.False(runner.WasRun("flat"));
    }

    [Fact]
    public async Task IncludeGlob_IsCaseInsensitive_AgainstLowercaseFiles()
    {
        WriteIndependentFlow("casing");
        var runner = new BecCountingRunner();

        // The on-disk file is casing.flow.yaml; the include pattern is upper-cased.
        var result = await RunAsync(MakeBatch(include: ["CASING.FLOW.YAML"]), runner);

        Assert.True(result.Success);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "casing").Status);
    }

    [Fact]
    public async Task ExcludeTakesPrecedence_WhenAFileMatchesBothIncludeAndExclude()
    {
        WriteIndependentFlow("keep");
        WriteIndependentFlow("drop");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(exclude: ["**/drop.flow.yaml"]), runner);

        Assert.Single(result.Members);
        Assert.Equal("keep", Member(result, "keep").FlowName);
        Assert.False(runner.WasRun("drop"));
    }

    [Fact]
    public async Task ExcludeWins_EvenWhenTheFileIsAlsoListedInactive()
    {
        WriteIndependentFlow("survivor");
        WriteIndependentFlow("gone");
        var runner = new BecCountingRunner();

        // gone is both excluded and named inactive; exclude removes it entirely, so it is not even reported inactive.
        var result = await RunAsync(MakeBatch(exclude: ["**/gone.flow.yaml"], inactive: ["**/gone.flow.yaml"]), runner);

        Assert.True(result.Success);
        Assert.Single(result.Members);
        Assert.Equal("survivor", Member(result, "survivor").FlowName);
    }

    [Fact]
    public async Task ExcludeGlob_CanRemoveSeveralMembersAtOnce()
    {
        WriteIngFlow("tmp_a", "S", "S.s.A", "S", "S.s.A2", subdirectory: "scratch");
        WriteIngFlow("tmp_b", "S", "S.s.B", "S", "S.s.B2", subdirectory: "scratch");
        WriteIndependentFlow("real");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(exclude: ["scratch/*.flow.yaml"]), runner);

        Assert.Single(result.Members);
        Assert.Equal("real", Member(result, "real").FlowName);
        Assert.False(runner.WasRun("tmp_a"));
        Assert.False(runner.WasRun("tmp_b"));
    }

    [Fact]
    public async Task IncludeUnionsMultiplePatterns()
    {
        WriteIngFlow("r1", "S", "S.s.In1", "S", "S.s.Out1", subdirectory: "raw");
        WriteIngFlow("d1", "S", "S.s.In2", "S", "S.s.Out2", subdirectory: "dim");
        WriteIngFlow("x1", "S", "S.s.In3", "S", "S.s.Out3", subdirectory: "other");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["raw/*.flow.yaml", "dim/*.flow.yaml"]), runner);

        Assert.Equal(2, result.Members.Count);
        Assert.True(runner.WasRun("r1"));
        Assert.True(runner.WasRun("d1"));
        Assert.False(runner.WasRun("x1"));
    }

    [Fact]
    public async Task AllMembersExcluded_FailsWithEmptyMembership()
    {
        WriteIndependentFlow("only");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(exclude: ["**/*.flow.yaml"]), runner);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Members);
        Assert.False(runner.WasRun("only"));
    }

    [Fact]
    public async Task NoMembersMatched_ErrorNamesTheIncludeGlob()
    {
        WriteIndependentFlow("present");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["no-such-folder/*.flow.yaml"]), runner);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("no-such-folder/*.flow.yaml", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Waves);
    }

    [Fact]
    public async Task BatchDocumentSibling_IsNeverSelectedAsAMember()
    {
        WriteIndependentFlow("real_member");
        // A sibling batch document in the same directory must never be treated as a member flow even when the
        // include glob is broad enough to match its file name.
        File.WriteAllText(Path.Combine(_dir, "nested.batch.yaml"), """
            flowType: batch
            name: nested
            members:
              include: ["*.flow.yaml"]
            """);
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(include: ["**/*.yaml"]), runner);

        Assert.True(result.Success);
        Assert.Single(result.Members);
        Assert.Equal("real_member", Member(result, "real_member").FlowName);
    }

    // ---- wave shape ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SingleIndependentMember_RunsInOneWave()
    {
        WriteIndependentFlow("solo");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.Success);
        Assert.Single(result.Waves);
        Assert.Equal(1, result.Waves[0].Wave);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "solo").Status);
        Assert.Equal(1, Member(result, "solo").Wave);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task LinearChain_ProducesOneWavePerLink(int length)
    {
        // step_i reads CHAIN.s.T{i} and writes CHAIN.s.T{i+1}; the next step reads what the previous wrote.
        for (var i = 0; i < length; i++)
        {
            WriteIngFlow(
                $"step_{i.ToString(CultureInfo.InvariantCulture)}",
                "CHAIN", $"CHAIN.s.T{i.ToString(CultureInfo.InvariantCulture)}",
                "CHAIN", $"CHAIN.s.T{(i + 1).ToString(CultureInfo.InvariantCulture)}");
        }

        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.Success);
        Assert.Equal(length, result.Waves.Count);
        Assert.Empty(result.Unordered);
        // Dependency dispatch puts each step strictly after its predecessor completed.
        for (var i = 1; i < length; i++)
        {
            Assert.True(runner.CompletionIndexOf($"step_{(i - 1).ToString(CultureInfo.InvariantCulture)}")
                < runner.StartIndexOf($"step_{i.ToString(CultureInfo.InvariantCulture)}"));
        }
    }

    [Fact]
    public async Task TwoIndependentChains_RunSideBySidePerWave()
    {
        // Chain one: a -> b. Chain two: c -> d. The two chains share no objects.
        WriteIngFlow("a", "G", "G.s.A0", "G", "G.s.A1");
        WriteIngFlow("b", "G", "G.s.A1", "G", "G.s.A2");
        WriteIngFlow("c", "G", "G.s.C0", "G", "G.s.C1");
        WriteIngFlow("d", "G", "G.s.C1", "G", "G.s.C2");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.Success);
        Assert.Equal(2, result.Waves.Count);
        Assert.Equal(1, Member(result, "a").Wave);
        Assert.Equal(1, Member(result, "c").Wave);
        Assert.Equal(2, Member(result, "b").Wave);
        Assert.Equal(2, Member(result, "d").Wave);
    }

    [Fact]
    public async Task ForkedEstate_OneProducerTwoConsumers_PlacesBothConsumersInTheSecondWave()
    {
        WriteIngFlow("producer", "F", "F.s.In", "F", "F.s.Shared");
        WriteIngFlow("consumer_one", "F", "F.s.Shared", "F", "F.s.One");
        WriteIngFlow("consumer_two", "F", "F.s.Shared", "F", "F.s.Two");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Equal(2, result.Waves.Count);
        Assert.Equal(1, Member(result, "producer").Wave);
        Assert.Equal(2, Member(result, "consumer_one").Wave);
        Assert.Equal(2, Member(result, "consumer_two").Wave);
        Assert.Equal(2, result.Waves[1].Members.Count);
    }

    [Fact]
    public async Task SelfReferentialFlow_DoesNotDependOnItself()
    {
        // Reads and writes the same object; the orchestrator must not create a self-edge.
        WriteIngFlow("self", "R", "R.s.Same", "R", "R.s.Same");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.Success);
        Assert.Single(result.Waves);
        Assert.Empty(result.Unordered);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "self").Status);
    }

    [Fact]
    public async Task CrossServerSameObjectName_DoesNotFormADependency()
    {
        // Both name "warehouse.s.T", but on different connections; distinct server identities mean no link.
        WriteIngFlow("writer", "DWONE", "DWONE.s.Src", "DWONE", "warehouse.s.T");
        WriteIngFlow("reader", "DWTWO", "warehouse.s.T", "DWTWO", "DWTWO.s.Out");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.Success);
        // No dependency, so both land in the first wave together.
        Assert.Single(result.Waves);
        Assert.Equal(1, Member(result, "writer").Wave);
        Assert.Equal(1, Member(result, "reader").Wave);
    }

    [Fact]
    public async Task WaveResult_ListsTheMembersScheduledIntoEachWave()
    {
        WriteIngFlow("up", "W", "W.s.In", "W", "W.s.Mid");
        WriteIngFlow("down", "W", "W.s.Mid", "W", "W.s.Out");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Equal(["up"], result.Waves[0].Members);
        Assert.Equal(["down"], result.Waves[1].Members);
    }

    [Fact]
    public async Task WaveMembers_AreReportedSortedByName()
    {
        // Two independent leaves of one producer share wave 2 and are emitted in case-insensitive name order.
        WriteIngFlow("root", "P", "P.s.In", "P", "P.s.Mid");
        WriteIngFlow("leaf_y", "P", "P.s.Mid", "P", "P.s.Y");
        WriteIngFlow("leaf_x", "P", "P.s.Mid", "P", "P.s.X");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Equal(["leaf_x", "leaf_y"], result.Waves[1].Members);
    }

    [Fact]
    public async Task Members_AreOrderedByWaveThenName()
    {
        WriteIngFlow("zeta", "O", "O.s.In", "O", "O.s.Mid");        // wave 1
        WriteIngFlow("omega", "O", "O.s.Mid", "O", "O.s.Out");      // wave 2 (depends on zeta)
        WriteIndependentFlow("aaa_first");                           // wave 1, sorts before zeta
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        // Wave 1 members come first, sorted by name within the wave, then wave 2.
        Assert.Equal(["aaa_first", "zeta", "omega"], result.Members.Select(m => m.FlowName).ToArray());
    }

    // ---- failure semantics --------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_FailureInLaterWave_SkipsOnlyTheStillLaterWave()
    {
        // a -> b -> c. The middle wave (b) fails under stop; a already ran, c is skipped.
        WriteIngFlow("a", "L", "L.s.T0", "L", "L.s.T1");
        WriteIngFlow("b", "L", "L.s.T1", "L", "L.s.T2");
        WriteIngFlow("c", "L", "L.s.T2", "L", "L.s.T3");
        var runner = new BecCountingRunner(fail: ["b"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop), runner);

        Assert.False(result.Success);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "a").Status);
        Assert.Equal(BatchMemberStatus.Failed, Member(result, "b").Status);
        Assert.Equal(BatchMemberStatus.Skipped, Member(result, "c").Status);
        Assert.True(runner.WasRun("a"));
        Assert.False(runner.WasRun("c"));
    }

    [Fact]
    public async Task Stop_SkippedMembers_RecordTheBatchStoppedReason()
    {
        WriteIngFlow("a", "L", "L.s.T0", "L", "L.s.T1");
        WriteIngFlow("b", "L", "L.s.T1", "L", "L.s.T2");
        WriteIngFlow("c", "L", "L.s.T2", "L", "L.s.T3");
        var runner = new BecCountingRunner(fail: ["a"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop), runner);

        // c is two waves past the failure; under stop it never starts and records why.
        var skipped = Member(result, "c");
        Assert.Equal(BatchMemberStatus.Skipped, skipped.Status);
        Assert.NotNull(skipped.Error);
        Assert.Contains("batch stopped", skipped.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Continue_TransitiveDependentsAreSkippedAcrossWaves()
    {
        // a -> b -> c. b fails under continue; c (a transitive dependent through one wave) is skipped.
        WriteIngFlow("a", "T", "T.s.T0", "T", "T.s.T1");
        WriteIngFlow("b", "T", "T.s.T1", "T", "T.s.T2");
        WriteIngFlow("c", "T", "T.s.T2", "T", "T.s.T3");
        var runner = new BecCountingRunner(fail: ["b"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue), runner);

        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "a").Status);
        Assert.Equal(BatchMemberStatus.Failed, Member(result, "b").Status);
        Assert.Equal(BatchMemberStatus.Skipped, Member(result, "c").Status);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Continue_SkippedDependent_RecordsTheBlockingDependency()
    {
        WriteIngFlow("producer", "T", "T.s.In", "T", "T.s.Mid");
        WriteIngFlow("consumer", "T", "T.s.Mid", "T", "T.s.Out");
        var runner = new BecCountingRunner(fail: ["producer"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue), runner);

        var skipped = Member(result, "consumer");
        Assert.Equal(BatchMemberStatus.Skipped, skipped.Status);
        Assert.Equal(2, skipped.Wave); // the wave it would have run in is preserved
        Assert.NotNull(skipped.Error);
        Assert.Contains("producer", skipped.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BatchErrorMode.Stop)]
    [InlineData(BatchErrorMode.Continue)]
    public async Task IndependentFailure_NeverBlocksAnUnrelatedMemberInTheSameWave(BatchErrorMode mode)
    {
        WriteIndependentFlow("victim");
        WriteIndependentFlow("bystander");
        var runner = new BecCountingRunner(fail: ["victim"]);

        var result = await RunAsync(MakeBatch(mode), runner);

        // Both are dispatched immediately (neither has a dependency), so the bystander is already in flight when
        // the victim's failure lands; stop never cancels in-flight members, so it runs to completion in either mode.
        Assert.Equal(BatchMemberStatus.Failed, Member(result, "victim").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "bystander").Status);
        Assert.True(runner.WasRun("bystander"));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task IgnoreErrors_UnderContinue_LetsDependentRun()
    {
        // raw fails but is ignorable; under continue its dependent must still run and succeed.
        WriteIngFlow("raw", "C", "C.s.In", "C", "C.s.Mid");
        WriteIngFlow("mart", "C", "C.s.Mid", "C", "C.s.Out");
        var runner = new BecCountingRunner(fail: ["raw"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue, ignoreErrors: ["**/raw.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "raw").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "mart").Status);
        Assert.True(runner.WasRun("mart"));
        Assert.True(result.Success);
    }

    [Fact]
    public async Task IgnoreErrors_AppliesToTheMidChainMember_KeepingTheTailRunning()
    {
        // a -> b -> c, b fails but is ignorable under stop; c depends on b and still runs.
        WriteIngFlow("a", "C", "C.s.T0", "C", "C.s.T1");
        WriteIngFlow("b", "C", "C.s.T1", "C", "C.s.T2");
        WriteIngFlow("c", "C", "C.s.T2", "C", "C.s.T3");
        var runner = new BecCountingRunner(fail: ["b"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop, ignoreErrors: ["**/b.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "a").Status);
        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "b").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "c").Status);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task IgnoreErrors_GlobMatchingEveryMember_KeepsTheBatchGreen()
    {
        WriteIndependentFlow("p");
        WriteIndependentFlow("q");
        var runner = new BecCountingRunner(fail: ["p", "q"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop, ignoreErrors: ["**/*.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "p").Status);
        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "q").Status);
        Assert.True(result.Success);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task FailedIgnored_DoesNotCountAsFailed_AndOtherMembersStillSucceed()
    {
        WriteIndependentFlow("good");
        WriteIndependentFlow("flaky");
        var runner = new BecCountingRunner(fail: ["flaky"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop, ignoreErrors: ["**/flaky.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "good").Status);
        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "flaky").Status);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunnerThatThrows_IsTreatedAsAFailedMember()
    {
        WriteIndependentFlow("boom");
        WriteIndependentFlow("calm");
        var runner = new BecThrowingRunner(throwFor: ["boom"], message: "kaboom");

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue), runner);

        var boom = Member(result, "boom");
        Assert.Equal(BatchMemberStatus.Failed, boom.Status);
        Assert.Equal("kaboom", boom.Error);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "calm").Status);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunnerThatThrows_ForAnIgnorableMember_IsFailedIgnored()
    {
        WriteIndependentFlow("explode");
        var runner = new BecThrowingRunner(throwFor: ["explode"], message: "ignored boom");

        var result = await RunAsync(MakeBatch(BatchErrorMode.Stop, ignoreErrors: ["**/explode.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "explode").Status);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DuplicateMemberNames_AcrossTwoFiles_FailWithAClearError()
    {
        // Two distinct files both declare name "twin". The orchestrator ships an explicit guard for exactly this
        // ("member flow name '...' is declared by N files ... names must be unique within a batch"), so the
        // intended contract is a clear failure that names neither file silently. This encodes that contract.
        // NOTE: lineage collapses same-named flows to the first file before the batch sees them, so this guard is
        // currently unreachable and one of the two files is run silently. Recorded as a suspected bug for triage.
        WriteIngFlow("twin", "S", "S.s.A", "S", "S.s.A2");
        WriteIngFlow("twin", "S", "S.s.B", "S", "S.s.B2", subdirectory: "elsewhere");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("twin", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Members);
    }

    // ---- inactive set -------------------------------------------------------------------------------------

    [Fact]
    public async Task AllMembersInactive_RunsNothing_ButSucceeds()
    {
        WriteIndependentFlow("one");
        WriteIndependentFlow("two");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(inactive: ["**/*.flow.yaml"]), runner);

        Assert.True(result.Success);
        Assert.Empty(result.Waves);
        Assert.Equal(2, result.Inactive);
        Assert.All(result.Members, m => Assert.Equal(BatchMemberStatus.Inactive, m.Status));
        Assert.False(runner.WasRun("one"));
        Assert.False(runner.WasRun("two"));
    }

    [Fact]
    public async Task InactiveMember_IsReportedInWaveZero_WithNoRunId()
    {
        WriteIndependentFlow("dormant");
        WriteIndependentFlow("active");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(inactive: ["**/dormant.flow.yaml"]), runner);

        var dormant = Member(result, "dormant");
        Assert.Equal(BatchMemberStatus.Inactive, dormant.Status);
        Assert.Equal(0, dormant.Wave);
        Assert.Null(dormant.RunId);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "active").Status);
    }

    [Fact]
    public async Task InactiveProducer_ShrinksTheWaveCount_AndDependentStillRuns()
    {
        // producer -> consumer would be two waves; deactivating the producer collapses it to a single wave.
        WriteIngFlow("producer", "I", "I.s.In", "I", "I.s.Mid");
        WriteIngFlow("consumer", "I", "I.s.Mid", "I", "I.s.Out");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(inactive: ["**/producer.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.Inactive, Member(result, "producer").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "consumer").Status);
        Assert.Single(result.Waves);
        Assert.Equal(1, Member(result, "consumer").Wave);
    }

    // ---- cycles -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoFlowMutualPair_IsBrokenByDeadlockAvoidance_AndRunsOrdered()
    {
        // a writes X and reads Y; b writes Y and reads X. The mutual pair is deliberately not a cycle: lineage
        // breaks one edge so the plan stays orderable, so neither is reported unordered.
        WriteIngFlow("a", "M", "M.s.Y", "M", "M.s.X");
        WriteIngFlow("b", "M", "M.s.X", "M", "M.s.Y");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Empty(result.Unordered);
        Assert.True(runner.WasRun("a"));
        Assert.True(runner.WasRun("b"));
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ThreeFlowCycle_UnderContinue_StillRunsEveryMember()
    {
        // A ring a -> b -> c -> a; the ring is undecidable and runs together in the final fallback wave.
        WriteIngFlow("a", "Y", "Y.s.X", "Y", "Y.s.Z");
        WriteIngFlow("b", "Y", "Y.s.Y", "Y", "Y.s.X");
        WriteIngFlow("c", "Y", "Y.s.Z", "Y", "Y.s.Y");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue), runner);

        Assert.Equal(3, result.Unordered.Count);
        Assert.True(runner.WasRun("a"));
        Assert.True(runner.WasRun("b"));
        Assert.True(runner.WasRun("c"));
    }

    [Fact]
    public async Task CycleMembers_ShareTheSameFallbackWave()
    {
        WriteIngFlow("a", "K", "K.s.X", "K", "K.s.Z");
        WriteIngFlow("b", "K", "K.s.Y", "K", "K.s.X");
        WriteIngFlow("c", "K", "K.s.Z", "K", "K.s.Y");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        // All three cycle members are unordered, so they were scheduled into one wave together.
        var distinctWaves = result.Members.Select(m => m.Wave).Distinct().ToList();
        Assert.Single(distinctWaves);
        Assert.Equal(3, result.Unordered.Count);
    }

    [Fact]
    public async Task CycleCoexistsWithAnOrderedChain_OnlyTheCycleIsUnordered()
    {
        WriteIngFlow("ring_a", "Y", "Y.s.X", "Y", "Y.s.Z");
        WriteIngFlow("ring_b", "Y", "Y.s.Y", "Y", "Y.s.X");
        WriteIngFlow("ring_c", "Y", "Y.s.Z", "Y", "Y.s.Y");
        WriteIngFlow("head", "Z", "Z.s.In", "Z", "Z.s.Mid");   // an ordered pair alongside the ring
        WriteIngFlow("tail", "Z", "Z.s.Mid", "Z", "Z.s.Out");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Equal(["ring_a", "ring_b", "ring_c"], result.Unordered);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "head").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "tail").Status);
        Assert.True(runner.CompletionIndexOf("head") < runner.StartIndexOf("tail"));
    }

    [Fact]
    public async Task CycleWarning_IsReported()
    {
        WriteIngFlow("a", "Q", "Q.s.X", "Q", "Q.s.Z");
        WriteIngFlow("b", "Q", "Q.s.Y", "Q", "Q.s.X");
        WriteIngFlow("c", "Q", "Q.s.Z", "Q", "Q.s.Y");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Contains(result.Warnings, w => w.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CycleUnorderedList_IsSortedDeterministically()
    {
        WriteIngFlow("c", "Q", "Q.s.Z", "Q", "Q.s.Y");
        WriteIngFlow("a", "Q", "Q.s.X", "Q", "Q.s.Z");
        WriteIngFlow("b", "Q", "Q.s.Y", "Q", "Q.s.X");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        var sorted = result.Unordered.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, result.Unordered);
    }

    // ---- concurrency bounds -------------------------------------------------------------------------------

    [Fact]
    public async Task MaxParallelOne_SerializesMembersWithinAWave()
    {
        // Three independent members in one wave; maxParallel 1 means the observed concurrency never exceeds one.
        WriteIndependentFlow("c1");
        WriteIndependentFlow("c2");
        WriteIndependentFlow("c3");
        var runner = new BecConcurrencyRunner(releaseAt: 1);

        var result = await RunAsync(MakeBatch(maxParallel: 1), runner);

        Assert.True(result.Success);
        Assert.Equal(1, runner.PeakConcurrency);
        Assert.Equal(3, runner.TotalRuns);
    }

    [Fact]
    public async Task MaxParallelTwo_CapsConcurrencyAtTwo()
    {
        WriteIndependentFlow("k1");
        WriteIndependentFlow("k2");
        WriteIndependentFlow("k3");
        WriteIndependentFlow("k4");
        // Exactly two members can be inside at once; the gate releases each pair as it forms.
        var runner = new BecConcurrencyRunner(releaseAt: 2);

        var result = await RunAsync(MakeBatch(maxParallel: 2), runner);

        Assert.True(result.Success);
        Assert.Equal(2, runner.PeakConcurrency);
        Assert.Equal(4, runner.TotalRuns);
    }

    [Fact]
    public async Task MaxParallelZero_UsesTheMachineSizedDefault_AndOverlapsMembers()
    {
        WriteIndependentFlow("u1");
        WriteIndependentFlow("u2");
        // maxParallel 0 defaults to Math.Max(2, ProcessorCount), so at least two members can be in flight on any
        // machine. releaseAt 2 means neither member completes until both have started; if the default serialized
        // them this would never reach two in flight, so PeakConcurrency proves they overlapped.
        var runner = new BecConcurrencyRunner(releaseAt: 2);

        var result = await RunAsync(MakeBatch(maxParallel: 0), runner);

        Assert.True(result.Success);
        Assert.Equal(2, runner.PeakConcurrency);
    }

    [Fact]
    public async Task MaxParallelLargerThanWave_BehavesAsUnbounded()
    {
        WriteIndependentFlow("w1");
        WriteIndependentFlow("w2");
        var runner = new BecConcurrencyRunner(releaseAt: 2);

        var result = await RunAsync(MakeBatch(maxParallel: 50), runner);

        Assert.True(result.Success);
        Assert.Equal(2, runner.PeakConcurrency);
    }

    // ---- cancellation, echoed fields, ids -----------------------------------------------------------------

    [Fact]
    public async Task PreCanceledToken_PropagatesOperationCanceled()
    {
        WriteIndependentFlow("never");
        var runner = new BecCountingRunner();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(MakeBatch(), runner, cts.Token));
    }

    [Theory]
    [InlineData(BatchErrorMode.Stop)]
    [InlineData(BatchErrorMode.Continue)]
    public async Task OnError_IsEchoedOnTheResult(BatchErrorMode mode)
    {
        WriteIndependentFlow("echoed");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(mode), runner);

        Assert.Equal(mode.ToString(), result.OnError);
    }

    [Fact]
    public async Task BatchName_IsEchoedFromTheFlow()
    {
        WriteIndependentFlow("named");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.Equal("batch-edge", result.BatchName);
    }

    [Fact]
    public async Task RunIds_ArePresentForRunMembers_AndNullForSkippedAndInactive()
    {
        WriteIngFlow("head", "D", "D.s.In", "D", "D.s.Mid");
        WriteIngFlow("tail", "D", "D.s.Mid", "D", "D.s.Out");
        WriteIndependentFlow("idle");
        var runner = new BecCountingRunner(fail: ["head"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue, inactive: ["**/idle.flow.yaml"]), runner);

        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.NotNull(Member(result, "head").RunId);   // ran (and failed)
        Assert.Null(Member(result, "tail").RunId);      // skipped
        Assert.Null(Member(result, "idle").RunId);      // inactive
    }

    [Fact]
    public async Task AggregateCounts_MatchTheMemberOutcomes_UnderContinue()
    {
        // good (wave 1) succeeds; bad (wave 1) fails; dep (wave 2, depends on bad) is skipped;
        // sleeper is inactive. One of each terminal state, so every counter is exercised.
        WriteIngFlow("good", "A", "A.s.G0", "A", "A.s.G1");
        WriteIngFlow("bad", "A", "A.s.B0", "A", "A.s.B1");
        WriteIngFlow("dep", "A", "A.s.B1", "A", "A.s.B2");
        WriteIndependentFlow("sleeper");
        var runner = new BecCountingRunner(fail: ["bad"]);

        var result = await RunAsync(MakeBatch(BatchErrorMode.Continue, inactive: ["**/sleeper.flow.yaml"]), runner);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Inactive);
        Assert.Equal(4, result.Members.Count);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DurationSeconds_IsNonNegative()
    {
        WriteIndependentFlow("timed");
        var runner = new BecCountingRunner();

        var result = await RunAsync(MakeBatch(), runner);

        Assert.True(result.DurationSeconds >= 0);
    }

    // ---- loader parsing edges (distinct from the existing loader suite) -----------------------------------

    [Theory]
    [InlineData("STOP", BatchErrorMode.Stop)]
    [InlineData("Continue", BatchErrorMode.Continue)]
    [InlineData("  continue  ", BatchErrorMode.Continue)]
    public void Loader_OnError_IsCaseAndWhitespaceInsensitive(string raw, BatchErrorMode expected)
    {
        var doc = new YamlBatchFlowLoader().Parse($"""
            flowType: batch
            name: parse
            members:
              include: ["*.flow.yaml"]
            onError: "{raw}"
            """);

        Assert.Equal(expected, doc.Flow.OnError);
    }

    [Theory]
    [InlineData("AUTO", BatchConnectMode.Auto)]
    [InlineData("Always", BatchConnectMode.Always)]
    [InlineData("never", BatchConnectMode.Never)]
    [InlineData("  Never  ", BatchConnectMode.Never)]
    public void Loader_Connect_IsCaseAndWhitespaceInsensitive(string raw, BatchConnectMode expected)
    {
        var doc = new YamlBatchFlowLoader().Parse($"""
            flowType: batch
            name: parse
            members:
              include: ["*.flow.yaml"]
            connect: "{raw}"
            """);

        Assert.Equal(expected, doc.Flow.Connect);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("offline")]
    [InlineData("yes")]
    public void Loader_UnknownConnect_IsRejected(string value)
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlBatchFlowLoader().Parse($"""
            flowType: batch
            name: bad
            members:
              include: ["*.flow.yaml"]
            connect: {value}
            """));

        Assert.Contains("connect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_EmptyIncludeList_IsRejected()
    {
        // The members.include key is present but the list is empty.
        var ex = Assert.Throws<FlowValidationException>(() => new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: bad
            members:
              include: []
            """));

        Assert.Contains("members.include", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_WhitespaceOnlyIncludeEntries_AreDroppedAndRejectedAsEmpty()
    {
        // Whitespace-only entries are cleaned away, leaving no usable glob.
        var ex = Assert.Throws<FlowValidationException>(() => new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: bad
            members:
              include:
                - "   "
                - ""
            """));

        Assert.Contains("members.include", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_TrimsAndDropsBlankGlobEntries_AcrossEveryList()
    {
        var doc = new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: trimmed
            members:
              include:
                - "  raw/*.flow.yaml  "
                - "   "
              exclude:
                - " raw/scratch.flow.yaml "
              inactive:
                - ""
                - " dim/legacy.flow.yaml"
            ignoreErrors:
              - "optional/*.flow.yaml "
              - "   "
            """);

        Assert.Equal(["raw/*.flow.yaml"], doc.Flow.Include);
        Assert.Equal(["raw/scratch.flow.yaml"], doc.Flow.Exclude);
        Assert.Equal(["dim/legacy.flow.yaml"], doc.Flow.Inactive);
        Assert.Equal(["optional/*.flow.yaml"], doc.Flow.IgnoreErrors);
    }

    [Fact]
    public void Loader_BlankDescription_BecomesNull()
    {
        var doc = new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: nodesc
            description: "   "
            members:
              include: ["*.flow.yaml"]
            """);

        Assert.Null(doc.Flow.Description);
    }

    [Fact]
    public void Loader_OmittedOptionalLists_DefaultToEmpty_NotNull()
    {
        var doc = new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: minimal
            members:
              include: ["*.flow.yaml"]
            """);

        Assert.Empty(doc.Flow.Exclude);
        Assert.Empty(doc.Flow.Inactive);
        Assert.Empty(doc.Flow.IgnoreErrors);
        Assert.Null(doc.Flow.Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(1024)]
    public void Loader_AcceptsZeroAndPositiveMaxParallel(int value)
    {
        var doc = new YamlBatchFlowLoader().Parse($"""
            flowType: batch
            name: ok
            members:
              include: ["*.flow.yaml"]
            maxParallel: {value.ToString(CultureInfo.InvariantCulture)}
            """);

        Assert.Equal(value, doc.Flow.MaxParallel);
    }

    [Fact]
    public void Loader_EmptyDocument_IsRejected()
    {
        // A document that deserializes to nothing is a clear validation error, not a null reference.
        var ex = Assert.Throws<FlowValidationException>(() => new YamlBatchFlowLoader().Parse("   "));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_StableFlowId_IsDeterministicForAName()
    {
        const string yaml = """
            flowType: batch
            name: repeatable
            members:
              include: ["*.flow.yaml"]
            """;

        var first = new YamlBatchFlowLoader().Parse(yaml).Flow.FlowId;
        var second = new YamlBatchFlowLoader().Parse(yaml).Flow.FlowId;

        Assert.Equal(first, second);
    }

    // ---- test doubles (uniquely named, self-contained) ----------------------------------------------------

    /// <summary>Records the start and completion order of each member so wave barriers and run/skip facts can be
    /// asserted; optionally fails a named subset by returning an unsuccessful outcome (never by throwing).
    /// Thread-safe for concurrent wave execution.</summary>
    private sealed class BecCountingRunner(IReadOnlyList<string>? fail = null) : IDocumentRunner
    {
        private readonly HashSet<string> _fail = new(fail ?? [], StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly List<string> _startOrder = [];
        private readonly List<string> _completeOrder = [];

        public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
        {
            var name = BecName(flowFile);
            lock (_lock)
            {
                _startOrder.Add(name);
            }

            await Task.Yield();
            lock (_lock)
            {
                _completeOrder.Add(name);
            }

            var fails = _fail.Contains(name);
            return new DocumentRunOutcome
            {
                FlowName = name,
                FlowKind = "ing",
                Success = !fails,
                Error = fails ? "fake failure" : null,
                RunId = Guid.NewGuid(),
            };
        }

        public bool WasRun(string name)
        {
            lock (_lock)
            {
                return _startOrder.Contains(name, StringComparer.OrdinalIgnoreCase);
            }
        }

        public int StartIndexOf(string name)
        {
            lock (_lock)
            {
                return _startOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public int CompletionIndexOf(string name)
        {
            lock (_lock)
            {
                return _completeOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>Throws for the named members instead of returning a failed outcome, to prove the orchestrator's
    /// guard turns an unexpected throw into a recorded member failure without aborting the wave.</summary>
    private sealed class BecThrowingRunner(IReadOnlyList<string> throwFor, string message) : IDocumentRunner
    {
        private readonly HashSet<string> _throw = new(throwFor, StringComparer.OrdinalIgnoreCase);

        public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
        {
            await Task.Yield();
            var name = BecName(flowFile);
            if (_throw.Contains(name))
            {
                throw new InvalidOperationException(message);
            }

            return new DocumentRunOutcome { FlowName = name, FlowKind = "ing", Success = true, RunId = Guid.NewGuid() };
        }
    }

    /// <summary>Measures the peak number of members running at once without blocking a thread. Each call records
    /// the live count, then awaits a shared gate that completes only once <paramref name="releaseAt"/> members
    /// have started; because the await truly suspends, the orchestrator's semaphore alone decides how many can be
    /// inside together, and the design cannot deadlock. Deterministic when the wave size is a multiple of
    /// <paramref name="releaseAt"/>.</summary>
    private sealed class BecConcurrencyRunner(int releaseAt) : IDocumentRunner
    {
        private readonly object _lock = new();
        private int _current;
        private int _started;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrency { get; private set; }

        public int TotalRuns { get; private set; }

        public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
        {
            Task gate;
            lock (_lock)
            {
                _current++;
                _started++;
                TotalRuns++;
                PeakConcurrency = Math.Max(PeakConcurrency, _current);
                if (_started % releaseAt == 0)
                {
                    _gate.TrySetResult();
                }

                gate = _gate.Task;
            }

            await gate.ConfigureAwait(false);

            lock (_lock)
            {
                _current--;
                if (_gate.Task.IsCompleted)
                {
                    // Reset for the next group of starts in this or a later wave.
                    _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            return new DocumentRunOutcome { FlowName = BecName(flowFile), FlowKind = "ing", Success = true, RunId = Guid.NewGuid() };
        }
    }

    private static string BecName(string flowFile)
        => Path.GetFileName(flowFile).Replace(".flow.yaml", string.Empty, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
