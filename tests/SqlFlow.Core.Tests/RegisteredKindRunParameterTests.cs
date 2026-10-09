using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Tests;

/// <summary>
/// A registered flow kind's own parameter check runs for every run of the kind, not only for a run that carries kind
/// arguments: a full load, a backfill window, a file pattern or a source filter that the kind does not apply is refused
/// by the kind at the loader's one rule, by the executor before the kind's executor runs, and by <c>sqlflow run</c>,
/// rather than being ignored while the run reports success. The kind here applies none of the built-in overrides.
/// The class reads what <c>sqlflow run</c> writes to the process-wide standard error, so it runs in a collection of
/// its own, alone, where no other test can swap the console under it or have its output swapped away.
/// </summary>
[Collection(RegisteredKindRunParameterTests.ConsoleCollection)]
public sealed class RegisteredKindRunParameterTests : IDisposable
{
    /// <summary>The collection that runs this class alone, after the parallel ones.</summary>
    public const string ConsoleCollection = "Registered kind run parameters on the console";

    private const string StrictFlow = """
        flowType: strict
        name: parcels
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("sqlflow-kind-parameters-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string, RunParameters> Overrides => new()
    {
        { "fullLoad", new RunParameters { FullLoad = true } },
        { "a backfill window", new RunParameters { BackfillFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } },
        { "filePattern", new RunParameters { FilePattern = "parcels_*.csv" } },
        { "sourceFilter", new RunParameters { SourceFilter = "AND id > 10" } },
        { "reprocessFromSourceMin", new RunParameters { ReprocessFromSourceMin = true } },
    };

    [Theory]
    [MemberData(nameof(Overrides))]
    public void TheLoadersRule_AsksTheKind_ForARunWithoutKindArguments(string named, RunParameters parameters)
    {
        var kind = new StrictFlowKind();

        var ex = Assert.Throws<SqlFlowException>(() => YamlDocumentLoader.CreateDefault([kind]).ValidateRunParameters("strict", parameters));

        Assert.False(parameters.HasKindArguments);
        Assert.Equal($"{named} does not apply to 'strict' flows; a strict flow reads its whole source on every run.", ex.Message);
        Assert.Single(kind.Checked);
    }

    [Fact]
    public void TheLoadersRule_AsksTheKind_ForADefaultRunToo()
    {
        var kind = new StrictFlowKind();

        YamlDocumentLoader.CreateDefault([kind]).ValidateRunParameters("STRICT", RunParameters.None);

        Assert.Same(RunParameters.None, Assert.Single(kind.Checked));
    }

    [Fact]
    public void ABuiltInKind_KeepsTheOverridesItApplies()
    {
        var loader = YamlDocumentLoader.CreateDefault([new StrictFlowKind()]);

        loader.ValidateRunParameters("ing", new RunParameters { FullLoad = true });
        loader.ValidateRunParameters("file", new RunParameters { FilePattern = "orders_*.csv" });
        loader.ValidateRunParameters("ing", new RunParameters { SourceFilter = "AND id > 10" });
    }

    [Fact]
    public async Task TheExecutor_RefusesAnOverrideTheKindDoesNotApply_BeforeTheKindsExecutorRuns()
    {
        var executor = new RecordingExecutor();
        var loader = YamlDocumentLoader.CreateDefault([new StrictFlowKind()]);
        using var provider = new ServiceCollection()
            .AddSingleton(loader)
            .AddSingleton<IFlowDocumentExecutor>(executor)
            .BuildServiceProvider();
        var document = loader.Parse(StrictFlow, "flows/parcels.yaml");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => new DocumentExecutor(provider).ExecuteAsync(
            document, "flows/parcels.yaml", new DocumentExecutionOptions { Parameters = new RunParameters { FullLoad = true } }));

        Assert.StartsWith("fullLoad does not apply to 'strict' flows", ex.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Ran);
    }

    [Fact]
    public async Task TheExecutor_RunsARegisteredDocument_WhoseKindAcceptsTheRun()
    {
        var executor = new RecordingExecutor();
        var kind = new StrictFlowKind();
        var loader = YamlDocumentLoader.CreateDefault([kind]);
        using var provider = new ServiceCollection()
            .AddSingleton(loader)
            .AddSingleton<IFlowDocumentExecutor>(executor)
            .BuildServiceProvider();

        var result = await new DocumentExecutor(provider).ExecuteAsync(
            loader.Parse(StrictFlow, "flows/parcels.yaml"), "flows/parcels.yaml", new DocumentExecutionOptions());

        Assert.True(result.Success);
        Assert.Equal(["parcels"], executor.Ran);
        Assert.Single(kind.Checked);
    }

    [Theory]
    [InlineData("fullLoad", new[] { "--full" })]
    [InlineData("a backfill window", new[] { "--from", "2026-01-01", "--to", "2026-02-01" })]
    [InlineData("filePattern", new[] { "--file-pattern", "parcels_*.csv" })]
    [InlineData("sourceFilter", new[] { "--source-filter", "AND id > 10" })]
    public async Task SqlflowRun_WithABackfillFlag_OnAFlowOfARegisteredKind_IsRefused_AndRunsNothing(string named, string[] flags)
    {
        var flow = Path.Combine(_dir, "parcels.yaml");
        await File.WriteAllTextAsync(flow, StrictFlow);
        var module = new StrictKindModule();

        var original = Console.Error;
        var said = new StringWriter();
        Console.SetError(said);
        int exit;
        try
        {
            exit = await CliHost.RunAsync(["run", flow, .. flags], module);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, exit);
        Assert.Contains($"ERROR  {named} does not apply to 'strict' flows", said.ToString(), StringComparison.Ordinal);
        Assert.Empty(module.Executor.Ran);
    }

    [Fact]
    public async Task SqlflowRun_WithoutABackfillFlag_RunsTheRegisteredKindsFlow()
    {
        var flow = Path.Combine(_dir, "parcels.yaml");
        await File.WriteAllTextAsync(flow, StrictFlow);
        var module = new StrictKindModule();

        var exit = await CliHost.RunAsync(["run", flow, "--json"], module);

        Assert.Equal(0, exit);
        Assert.Equal(["parcels"], module.Executor.Ran);
    }

    /// <summary>A CLI module that registers the strict kind and its executor, as a host module registers its own.</summary>
    private sealed class StrictKindModule : ICliModule
    {
        public RecordingExecutor Executor { get; } = new();

        public string Name => "strict";

        public IReadOnlyList<CliVerb> Verbs => [];

        public void ConfigureServices(CliModuleServices services)
        {
            services.Services.AddSingleton<IFlowDocumentKind>(new StrictFlowKind());
            services.Services.AddSingleton<IFlowDocumentExecutor>(Executor);
        }
    }

    private sealed class RecordingExecutor : IFlowDocumentExecutor
    {
        public List<string> Ran { get; } = [];

        public bool CanExecute(RegisteredFlowDocument document) => document is StrictFlowDocument;

        public Task<DocumentExecutionResult> ExecuteAsync(
            RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
        {
            Ran.Add(document.Name);
            return Task.FromResult(new DocumentExecutionResult
            {
                FlowName = document.Name,
                FlowKind = document.Kind,
                Success = true,
                Result = new { document.Name },
            });
        }
    }

    private sealed record StrictFlowDocument(string FlowName) : RegisteredFlowDocument
    {
        public override string Name => FlowName;

        public override string Kind => "strict";
    }

    /// <summary>A test-only kind (<c>flowType: strict</c>) that applies none of the built-in overrides, so its check
    /// refuses each by name, and records every run it was asked about.</summary>
    private sealed class StrictFlowKind : IFlowDocumentKind
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public List<RunParameters> Checked { get; } = [];

        public string FlowType => "strict";

        public string Description => "a test-only flow that reads its whole source every run";

        public IReadOnlyList<FlowKindOperation> Operations => [];

        public RegisteredFlowDocument Parse(string yaml, string source)
        {
            Body body;
            try
            {
                body = Deserializer.Deserialize<Body>(yaml) ?? new Body();
            }
            catch (YamlException ex)
            {
                throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
            }

            return string.IsNullOrWhiteSpace(body.Name)
                ? throw new FlowValidationException($"{source}: a strict flow requires a 'name'.")
                : new StrictFlowDocument(body.Name);
        }

        public void ValidateParameters(RunParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            Checked.Add(parameters);
            var named = new List<string>();
            if (parameters.FullLoad)
            {
                named.Add("fullLoad");
            }

            if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null)
            {
                named.Add("a backfill window");
            }

            if (parameters.FilePattern is not null)
            {
                named.Add("filePattern");
            }

            if (parameters.SourceFilter is not null)
            {
                named.Add("sourceFilter");
            }

            if (parameters.ReprocessFromSourceMin)
            {
                named.Add("reprocessFromSourceMin");
            }

            if (named.Count > 0)
            {
                throw new SqlFlowException(
                    $"{string.Join(", ", named)} does not apply to 'strict' flows; a strict flow reads its whole source on every run.");
            }
        }

        private sealed class Body
        {
            public string? Name { get; set; }
        }
    }
}

/// <summary>Runs <see cref="RegisteredKindRunParameterTests"/> alone, after the parallel collections, because it
/// redirects the process-wide standard error.</summary>
[CollectionDefinition(RegisteredKindRunParameterTests.ConsoleCollection, DisableParallelization = true)]
public sealed class RegisteredKindRunParameterConsoleGroup
{
}
