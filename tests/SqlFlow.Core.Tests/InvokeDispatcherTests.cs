using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.SqlServer.Invoke;
using Xunit;

namespace SqlFlow.Tests;

public sealed class InvokeDispatcherTests
{
    private static InvokeDefinition Def(InvokeType type = InvokeType.AzureAutomation) => new()
    {
        FlowId = 1,
        InvokeAlias = "a",
        InvokeType = type,
    };

    [Fact]
    public async Task NoExecutorRegistered_ReportsFailure_WithoutThrowing()
    {
        // The shipped host registers no executors yet (adf/aut are deferred drop-ins), so every type is reported
        // as a clear failure rather than throwing.
        var dispatcher = new InvokeDispatcher([]);

        var result = await dispatcher.DispatchAsync(Def(InvokeType.AzureDataFactory));

        Assert.False(result.Success);
        Assert.Contains("not supported", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adf", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoutesToHandlingExecutor_AndCapturesOutput()
    {
        var dispatcher = new InvokeDispatcher(
        [
            new FakeExecutor(InvokeType.AzureDataFactory, _ => throw new InvalidOperationException("wrong executor")),
            new FakeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution { StandardOutput = "Completed" }),
        ]);

        var result = await dispatcher.DispatchAsync(Def());

        Assert.True(result.Success, result.Error);
        Assert.Equal("Completed", result.StandardOutput);
        Assert.Equal(InvokeType.AzureAutomation, result.InvokeType);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ExecutorThrow_BecomesFailedResult()
    {
        var dispatcher = new InvokeDispatcher([new FakeExecutor(InvokeType.AzureAutomation, _ => throw new InvalidOperationException("boom"))]);

        var result = await dispatcher.DispatchAsync(Def());

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_WithAssignedRunId_StampsThatExactId()
    {
        // The control plane mints a run id and hands it to the caller before the run executes; the dispatcher must
        // record the run under that exact id (not a freshly minted one) so GET /api/v1/runs/{id} resolves.
        var assigned = Guid.NewGuid();
        var dispatcher = new InvokeDispatcher([new FakeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution { StandardOutput = "ok" })]);

        var result = await dispatcher.DispatchAsync(Def(), assigned);

        Assert.Equal(assigned, result.RunId);
    }

    [Fact]
    public async Task DispatchAsync_WithoutAssignedRunId_MintsAFreshIdPerCall()
    {
        // No assigned id (a nested pre/post-invoke hook): the dispatcher owns the identity, minting a distinct id
        // each call, exactly as before the assigned-id seam was added.
        var dispatcher = new InvokeDispatcher([new FakeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution())]);

        var first = await dispatcher.DispatchAsync(Def());
        var second = await dispatcher.DispatchAsync(Def());

        Assert.NotEqual(Guid.Empty, first.RunId);
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task InvokeFlowRunner_ThreadsAssignedRunIdFromOptionsToResult()
    {
        // The orchestrator-assigned id travels options -> top-level runner -> dispatcher, so a triggered invoke
        // flow records under the id the trigger returned. This is the IngestionRunOptions.RunId contract the
        // ingestion/export/stored-procedure/health-check runners share.
        var assigned = Guid.NewGuid();
        var runner = WithoutDatabaseInvoke.BuildRunner([new FakeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution())]);

        var result = await runner.RunAsync(Def(), new IngestionRunOptions { RunId = assigned });

        Assert.Equal(assigned, result.RunId);
    }

    private sealed class FakeExecutor : IInvokeExecutor
    {
        private readonly InvokeType _type;
        private readonly Func<InvokeDefinition, InvokeExecution> _run;

        public FakeExecutor(InvokeType type, Func<InvokeDefinition, InvokeExecution> run)
        {
            _type = type;
            _run = run;
        }

        public bool CanHandle(InvokeType type) => type == _type;

        public Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default)
            => Task.FromResult(_run(definition));
    }
}
