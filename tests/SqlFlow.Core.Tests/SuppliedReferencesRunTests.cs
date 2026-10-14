using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A run's supplied references end to end on the engine side: they ride on the run's parameters without making the run an
/// override or giving it kind arguments, a node task's payload carries them through its stored form, and the document
/// executor resolves every reference of the run it executes with them, for every kind (a registered kind here, which reads
/// its reference through the engine's own resolver), saying on the run's trace where each value came from.
/// </summary>
public sealed class SuppliedReferencesRunTests
{
    private const string ProbeFlow = """
        flowType: probe
        name: wells
        source: s3://drops/wells/
        sourceConnection: ${env:WAREHOUSE_DB}
        target: https://platform.example/api/storage/v2/records
        """;

    private static readonly Dictionary<string, string> Warehouse = new(StringComparer.Ordinal)
    {
        ["WAREHOUSE_DB"] = "Server=tcp:sql.example.net;Database=dw;Authentication=Active Directory Default",
    };

    // ---- On the run's parameters ----------------------------------------------------------------------------------

    [Fact]
    public void References_AreNeitherAnOverrideNorAKindArgument()
    {
        var parameters = new RunParameters { References = Warehouse };

        Assert.True(parameters.IsDefault);
        Assert.False(parameters.HasKindArguments);
        parameters.Validate();
        Assert.Equal("references WAREHOUSE_DB", parameters.Describe());
        Assert.Equal("full load, references WAREHOUSE_DB", (parameters with { FullLoad = true }).Describe());
    }

    [Fact]
    public void ARunCarryingAWrittenOutCredential_IsRefused()
    {
        var parameters = new RunParameters
        {
            References = new Dictionary<string, string>(StringComparer.Ordinal) { ["WAREHOUSE_DB"] = "Server=sql;Password=hunter2" },
        };

        var ex = Assert.Throws<SqlFlowException>(parameters.Validate);

        Assert.StartsWith("The run's references give WAREHOUSE_DB a connection string with a password", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    // ---- On a node task -------------------------------------------------------------------------------------------

    [Fact]
    public void ATasksReferences_SurviveItsStoredForm()
    {
        var payload = new ComputeTaskPayload { Operation = ComputeOperations.TestConnection, SourceRef = "${env:WAREHOUSE_DB}", References = Warehouse };

        var read = ComputeTaskPayload.FromJson(payload.ToJson());

        Assert.Equal(Warehouse, read.References);
        Assert.Contains("\"references\":{\"WAREHOUSE_DB\":", payload.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATaskWithoutReferences_ReadsBackWithNone()
    {
        var payload = new ComputeTaskPayload { Operation = ComputeOperations.TestConnection, SourceRef = "${env:WAREHOUSE_DB}" };

        Assert.DoesNotContain("references", payload.ToJson(), StringComparison.Ordinal);
        Assert.Null(ComputeTaskPayload.FromJson(payload.ToJson()).References);
    }

    [Fact]
    public void ATaskCarryingAWrittenOutCredential_IsRefused()
    {
        var json = """{"operation":"testConnection","sourceRef":"${env:WAREHOUSE_DB}","references":{"GIT_TOKEN":"abc"}}""";

        var ex = Assert.Throws<SqlFlowException>(() => ComputeTaskPayload.FromJson(json));

        Assert.StartsWith("The task's references give GIT_TOKEN a value that is not a reference", ex.Message, StringComparison.Ordinal);
    }

    // ---- In the run the executor executes -------------------------------------------------------------------------

    [Fact]
    public async Task TheExecutor_ResolvesTheRunsReferencesWithTheSuppliedValues_AndSaysWhereEachCameFrom()
    {
        var executor = new ResolvingExecutor();
        using var provider = Provider(executor);
        var events = new List<FlowEvent>();
        var options = new DocumentExecutionOptions
        {
            Parameters = new RunParameters { References = Warehouse },
            EventSink = new ListSink(events),
        };

        var result = await new DocumentExecutor(provider).ExecuteAsync(Loader().Parse(ProbeFlow, "flows/wells.yaml"), "flows/wells.yaml", options);

        Assert.True(result.Success);
        Assert.Equal(Warehouse["WAREHOUSE_DB"], executor.Resolved);
        Assert.Equal("env:PLATFORM_URL", executor.Platform);
        Assert.Equal(
            [
                "Reference WAREHOUSE_DB takes its value from the configuration the control plane supplied with this run.",
                "Reference PLATFORM_URL takes its value from this node's environment.",
            ],
            events.Where(e => e.Stage == DocumentExecutionOptions.ReferenceStage).Select(e => e.Message));
        Assert.Empty(SuppliedReferences.Current);
    }

    [Fact]
    public async Task TheExecutor_OfARunGivenNothing_ResolvesFromTheEnvironment_AndSaysNothingOfIt()
    {
        var executor = new ResolvingExecutor();
        using var provider = Provider(executor);
        var events = new List<FlowEvent>();

        var result = await new DocumentExecutor(provider).ExecuteAsync(
            Loader().Parse(ProbeFlow, "flows/wells.yaml"), "flows/wells.yaml", new DocumentExecutionOptions { EventSink = new ListSink(events) });

        Assert.True(result.Success);
        Assert.Equal("env:WAREHOUSE_DB", executor.Resolved);
        Assert.DoesNotContain(events, e => e.Stage == DocumentExecutionOptions.ReferenceStage);
    }

    [Fact]
    public async Task TheExecutor_RefusesARunCarryingAWrittenOutCredential_BeforeTheKindRuns()
    {
        var executor = new ResolvingExecutor();
        using var provider = Provider(executor);
        var options = new DocumentExecutionOptions
        {
            Parameters = new RunParameters { References = new Dictionary<string, string>(StringComparer.Ordinal) { ["PLATFORM_TOKEN"] = "abc" } },
        };

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => new DocumentExecutor(provider).ExecuteAsync(Loader().Parse(ProbeFlow, "flows/wells.yaml"), "flows/wells.yaml", options));

        Assert.Contains("PLATFORM_TOKEN names a credential", ex.Message, StringComparison.Ordinal);
        Assert.Null(executor.Resolved);
    }

    private static YamlDocumentLoader Loader() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind()], [new ProbeMappingKind()]);

    private static ServiceProvider Provider(ResolvingExecutor executor)
        => new ServiceCollection()
            .AddSingleton(Loader())
            .AddSingleton<ISecretProvider>(new EchoProvider("env"))
            .AddSingleton<ISecretResolver, SecretResolver>()
            .AddSingleton<IFlowDocumentExecutor>(executor)
            .BuildServiceProvider();

    /// <summary>
    /// A kind's executor that resolves its document's source connection and one reference of its own through the engine's
    /// resolver, as a real kind reads its endpoints, and publishes through the run's own event collector.
    /// </summary>
    private sealed class ResolvingExecutor : IFlowDocumentExecutor
    {
        public string? Resolved { get; private set; }

        public string? Platform { get; private set; }

        public bool CanExecute(RegisteredFlowDocument document) => true;

        public async Task<DocumentExecutionResult> ExecuteAsync(
            RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
        {
            var events = options.CreateEventCollector();
            var resolver = new SecretResolver([new EchoProvider("env")]);
            Resolved = await resolver.ResolveAsync("${env:WAREHOUSE_DB}", ct);
            Platform = resolver.Resolve("${env:PLATFORM_URL}");
            resolver.Resolve("${env:WAREHOUSE_DB}");
            events.Publish(new FlowEvent { FlowName = document.Name, Message = "done", Stage = "probe.done" });
            return new DocumentExecutionResult { FlowName = document.Name, FlowKind = document.Kind, Success = true, Result = document };
        }
    }

    private sealed class ListSink(List<FlowEvent> events) : IFlowEventSink
    {
        public void Publish(FlowEvent flowEvent)
        {
            lock (events)
            {
                events.Add(flowEvent);
            }
        }
    }

    private sealed class EchoProvider(string scheme) : ISecretProvider
    {
        public string Scheme => scheme;

        public string Resolve(string locator) => $"{scheme}:{locator}";
    }
}
