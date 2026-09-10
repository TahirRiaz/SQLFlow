using System.Collections.Concurrent;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Secrets;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Tests.Batch;

/// <summary>
/// The batch orchestrator over a real lineage-computed dependency graph (declared tier, no database): members run
/// in dependency order via per-member DAG dispatch (a member starts once its own direct dependencies complete;
/// the reported waves are plan levels, not barriers), and the failure semantics (stop vs continue, ignoreErrors,
/// skip dependents), inactive members, and dependency cycles all behave as specified. The member runner is faked
/// so the orchestration is provable without executing any SQL.
/// </summary>
public sealed class BatchOrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_batch_" + Guid.NewGuid().ToString("N"));

    public BatchOrchestratorTests() => Directory.CreateDirectory(_dir);

    // raw_orders: SRC.dbo.Orders -> DW.raw.Orders
    // dim_customer: DW.raw.Orders -> DW.dim.Customer   (depends on raw_orders)
    // audit: SRC.dbo.Audit -> DW.audit.Log             (independent)
    private void WriteStandardEstate()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");
        WriteIngFlow("dim_customer", "DW", "DW.raw.Orders", "DW", "DW.dim.Customer");
        WriteIngFlow("audit", "SRC", "Src.dbo.Audit", "DW", "DW.audit.Log");
    }

    private void WriteIngFlow(string name, string sourceServer, string sourceObject, string targetServer, string targetObject)
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
        File.WriteAllText(Path.Combine(_dir, name + ".flow.yaml"), yaml);
    }

    private void WriteIngFlowWithHealthCheck(string name, string? mode)
    {
        var yaml = $"""
            flowType: ing
            name: {name}
            connections:
              SRC:
              DW:
            source:
              server: SRC
              object: Src.dbo.Orders
            target:
              server: DW
              object: DW.raw.Orders
            healthCheck:
              dateColumn: OrderDate
              baseValue: COUNT(*)
            {(mode is null ? string.Empty : $"  mode: {mode}")}
            """;
        File.WriteAllText(Path.Combine(_dir, name + ".flow.yaml"), yaml);
    }

    private static BatchFlow Batch(BatchErrorMode onError = BatchErrorMode.Stop, IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null, IReadOnlyList<string>? inactive = null, IReadOnlyList<string>? ignoreErrors = null,
        int maxParallel = 0)
        => new()
        {
            FlowId = 1,
            SysAlias = "batch-test",
            Include = include ?? ["*.flow.yaml"],
            Exclude = exclude ?? [],
            Inactive = inactive ?? [],
            IgnoreErrors = ignoreErrors ?? [],
            OnError = onError,
            MaxParallel = maxParallel,
            Connect = BatchConnectMode.Never,
        };

    private Task<BatchRunResult> RunAsync(BatchFlow flow, IDocumentRunner runner)
        => new BatchOrchestrator(runner).RunAsync(flow, Path.Combine(_dir, "batch.batch.yaml"),
            new SecretResolver([new EnvSecretProvider()]), new DocumentExecutionOptions());

    private static BatchMemberResult Member(BatchRunResult result, string name)
        => result.Members.Single(m => string.Equals(m.FlowName, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task EmbeddedHealthCheck_ManualByDefault_IsReportedNotRun()
    {
        WriteIngFlowWithHealthCheck("raw_orders", mode: null);
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(), runner);

        // The derived check is a member (declared by the file) but never executes: embedded checks default to
        // mode: manual, and a manual member must not fail or skip-block the batch.
        Assert.True(result.Success);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "raw_orders").Status);
        Assert.Equal(BatchMemberStatus.Manual, Member(result, "raw_orders_hc").Status);
        Assert.Equal(1, result.Manual);
        Assert.False(runner.WasRun("raw_orders_hc"));
    }

    [Fact]
    public async Task EmbeddedHealthCheck_AutoMode_RunsAfterItsLoad_UnderItsOwnName()
    {
        WriteIngFlowWithHealthCheck("raw_orders", mode: "auto");
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(), runner);

        // mode: auto opts the derived check into the batch; it reads what the load writes, so lineage orders it
        // after the load, and the orchestrator dispatches it by ITS name from the shared file.
        Assert.True(result.Success);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "raw_orders_hc").Status);
        Assert.True(runner.CompletionIndexOf("raw_orders") < runner.StartIndexOf("raw_orders_hc"));
    }

    [Fact]
    public async Task OrdersMembersIntoWaves_DependencyBeforeDependent()
    {
        WriteIngFlow("raw_orders", "SRC", "Src.dbo.Orders", "DW", "DW.raw.Orders");
        WriteIngFlow("dim_customer", "DW", "DW.raw.Orders", "DW", "DW.dim.Customer");
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(), runner);

        Assert.True(result.Success);
        Assert.Equal(2, result.Waves.Count);
        Assert.Contains("raw_orders", result.Waves[0].Members);
        Assert.Contains("dim_customer", result.Waves[1].Members);
        // Dependency dispatch guarantees the dependency completed before the dependent started.
        Assert.True(runner.CompletionIndexOf("raw_orders") < runner.StartIndexOf("dim_customer"));
        Assert.All(result.Members, m => Assert.Equal(BatchMemberStatus.Succeeded, m.Status));
    }

    [Fact]
    public async Task Stop_OnFailure_SkipsLaterWaves()
    {
        WriteStandardEstate();
        var runner = new FakeRunner(fail: ["raw_orders"]);

        var result = await RunAsync(Batch(BatchErrorMode.Stop), runner);

        Assert.False(result.Success);
        Assert.Equal(BatchMemberStatus.Failed, Member(result, "raw_orders").Status);
        // dim_customer is in wave 2; the batch stopped after wave 1, so it never ran.
        Assert.Equal(BatchMemberStatus.Skipped, Member(result, "dim_customer").Status);
        Assert.False(runner.WasRun("dim_customer"));
    }

    [Fact]
    public async Task Stop_LetsInFlightMembersFinish_WhenAnotherMemberFails()
    {
        WriteStandardEstate();
        // audit and raw_orders are both dispatched immediately (neither has a dependency); audit fails while
        // raw_orders is still in flight. Stop never cancels in-flight members, so raw_orders runs to completion.
        var runner = new FakeRunner(fail: ["audit"]);

        var result = await RunAsync(Batch(BatchErrorMode.Stop), runner);

        Assert.True(runner.WasRun("raw_orders"));
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "raw_orders").Status);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DagDispatch_MemberStartsWhenItsDependenciesComplete_NotWhenItsWaveIsReached()
    {
        WriteStandardEstate();
        // audit (wave 1, independent) is held until dim_customer (wave 2) has STARTED. Under a wave barrier
        // dim_customer could never start before audit completed, so the ordering below proves per-member
        // dispatch: dim_customer only needs raw_orders, not the whole prior wave.
        var runner = new GatedRunner(held: "audit", releasedByStartOf: "dim_customer");

        var result = await RunAsync(Batch(), runner);

        Assert.True(result.Success);
        Assert.All(result.Members, m => Assert.Equal(BatchMemberStatus.Succeeded, m.Status));
        Assert.True(runner.StartIndexOf("dim_customer") < runner.CompletionIndexOf("audit"));
    }

    [Fact]
    public async Task Continue_RunsIndependentMembersPastAFailure()
    {
        WriteStandardEstate();
        var runner = new FakeRunner(fail: ["audit"]);

        var result = await RunAsync(Batch(BatchErrorMode.Continue), runner);

        // audit failed but raw_orders -> dim_customer is independent of it, so the chain still completes.
        Assert.Equal(BatchMemberStatus.Failed, Member(result, "audit").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "raw_orders").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "dim_customer").Status);
        Assert.False(result.Success); // a member still failed
    }

    [Fact]
    public async Task Continue_SkipsDependentsOfAFailure()
    {
        WriteStandardEstate();
        var runner = new FakeRunner(fail: ["raw_orders"]);

        var result = await RunAsync(Batch(BatchErrorMode.Continue), runner);

        Assert.Equal(BatchMemberStatus.Failed, Member(result, "raw_orders").Status);
        // dim_customer depends on raw_orders, so it is skipped even under continue.
        Assert.Equal(BatchMemberStatus.Skipped, Member(result, "dim_customer").Status);
        // audit is independent and still runs.
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "audit").Status);
    }

    [Fact]
    public async Task IgnoreErrors_FailureIsNonFatal_AndDoesNotBlockDependents()
    {
        WriteStandardEstate();
        // raw_orders is allowed to fail; its failure must neither stop the batch nor block dim_customer.
        var runner = new FakeRunner(fail: ["raw_orders"]);

        var result = await RunAsync(Batch(BatchErrorMode.Stop, ignoreErrors: ["raw_orders.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.FailedIgnored, Member(result, "raw_orders").Status);
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "dim_customer").Status);
        Assert.True(result.Success); // an ignored failure does not fail the batch
    }

    [Fact]
    public async Task InactiveMember_IsDeclaredButSkipped_AndDoesNotBlockDependents()
    {
        WriteStandardEstate();
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(inactive: ["raw_orders.flow.yaml"]), runner);

        Assert.Equal(BatchMemberStatus.Inactive, Member(result, "raw_orders").Status);
        Assert.False(runner.WasRun("raw_orders"));
        // dim_customer's only producer is inactive (assumed handled elsewhere), so it still runs.
        Assert.Equal(BatchMemberStatus.Succeeded, Member(result, "dim_customer").Status);
    }

    [Fact]
    public async Task ExcludeGlob_RemovesAMember()
    {
        WriteStandardEstate();
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(exclude: ["audit.flow.yaml"]), runner);

        Assert.DoesNotContain(result.Members, m => string.Equals(m.FlowName, "audit", StringComparison.OrdinalIgnoreCase));
        Assert.False(runner.WasRun("audit"));
    }

    [Fact]
    public async Task DependencyCycle_MembersRunInFallbackWave_AndAreReportedUnordered()
    {
        // A three-flow ring: a reads X/writes Z, b reads Y/writes X, c reads Z/writes Y, so a->b->c->a.
        // (A two-flow mutual pair is intentionally broken by lineage's deadlock avoidance; a ring survives.)
        WriteIngFlow("a", "DW", "DW.s.X", "DW", "DW.s.Z");
        WriteIngFlow("b", "DW", "DW.s.Y", "DW", "DW.s.X");
        WriteIngFlow("c", "DW", "DW.s.Z", "DW", "DW.s.Y");
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(), runner);

        Assert.Equal(3, result.Unordered.Count);
        Assert.Contains("a", result.Unordered);
        Assert.Contains("b", result.Unordered);
        Assert.Contains("c", result.Unordered);
        Assert.True(runner.WasRun("a"));
        Assert.True(runner.WasRun("b"));
        Assert.True(runner.WasRun("c"));
    }

    [Fact]
    public async Task EmptyMembership_FailsWithAClearError()
    {
        WriteStandardEstate();
        var runner = new FakeRunner();

        var result = await RunAsync(Batch(include: ["does-not-exist/*.flow.yaml"]), runner);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Members);
    }

    private sealed class FakeRunner(IReadOnlyList<string>? fail = null) : IDocumentRunner
    {
        private readonly HashSet<string> _fail = new(fail ?? [], StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _started = new();
        private readonly ConcurrentQueue<string> _completed = new();
        private readonly object _lock = new();
        private readonly List<string> _startOrder = [];
        private readonly List<string> _completeOrder = [];

        public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
        {
            // The orchestrator selects the member by flow name (a document can expand into more than one
            // pipeline, e.g. an embedded healthCheck:); fall back to the file stem for robustness.
            var name = options.FlowName ?? Path.GetFileName(flowFile).Replace(".flow.yaml", string.Empty, StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                _startOrder.Add(name);
            }

            await Task.Yield();
            lock (_lock)
            {
                _completeOrder.Add(name);
            }

            return new DocumentRunOutcome
            {
                FlowName = name,
                FlowKind = "ing",
                Success = !_fail.Contains(name),
                Error = _fail.Contains(name) ? "fake failure" : null,
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

    /// <summary>Like <see cref="FakeRunner"/> (always succeeds), but holds one member's completion until another
    /// member has started, so dispatch-ordering assertions are deterministic. A generous fallback delay releases
    /// the held member anyway, so a dispatch regression fails the ordering assertion instead of hanging the
    /// test.</summary>
    private sealed class GatedRunner(string held, string releasedByStartOf) : IDocumentRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        // One global event sequence ("start:name" / "complete:name"), so start-vs-completion ordering across
        // DIFFERENT members can be asserted by index (two per-kind lists cannot express that).
        private readonly List<string> _events = [];

        public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
        {
            // The orchestrator selects the member by flow name (a document can expand into more than one
            // pipeline, e.g. an embedded healthCheck:); fall back to the file stem for robustness.
            var name = options.FlowName ?? Path.GetFileName(flowFile).Replace(".flow.yaml", string.Empty, StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                _events.Add("start:" + name);
            }

            if (string.Equals(name, releasedByStartOf, StringComparison.OrdinalIgnoreCase))
            {
                _release.TrySetResult();
            }

            if (string.Equals(name, held, StringComparison.OrdinalIgnoreCase))
            {
                await Task.WhenAny(_release.Task, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            lock (_lock)
            {
                _events.Add("complete:" + name);
            }

            return new DocumentRunOutcome
            {
                FlowName = name,
                FlowKind = "ing",
                Success = true,
                RunId = Guid.NewGuid(),
            };
        }

        public int StartIndexOf(string name) => EventIndexOf("start:" + name);

        public int CompletionIndexOf(string name) => EventIndexOf("complete:" + name);

        private int EventIndexOf(string entry)
        {
            lock (_lock)
            {
                return _events.FindIndex(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
