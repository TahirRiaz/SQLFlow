using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Tests;

/// <summary>
/// The flow kind registry: a host registers a kind of its own (<see cref="IFlowDocumentKind"/>), a companion document
/// type (<see cref="ICompanionDocumentKind"/>) and an executor (<see cref="IFlowDocumentExecutor"/>), and every
/// platform consumer (the loader, the header projection, the declared endpoints, the estate scan, discovery, the
/// secret-hygiene check and the document executor) handles the kind exactly as it handles a built-in one. The kind
/// here is test-only and executes nothing.
/// </summary>
public sealed class FlowKindRegistryTests : IDisposable
{
    private const string ProbeFlow = """
        flowType: probe
        name: wells
        batch: subsurface
        schedule:
          cron: "0 6 * * *"
          timezone: "Europe/Oslo"
        mode: manual
        source: s3://drops/wells/
        sourceConnection: ${env:SQLFLOW_CONN_WELLS}
        target: https://platform.example/api/storage/v2/records
        credentials:
          platform: ${env:PLATFORM_TOKEN}
        """;

    private const string ProbeMapping = """
        documentType: probe-mapping
        name: wells-mapping
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow-kind-registry-" + Guid.NewGuid().ToString("N"));

    public FlowKindRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static YamlDocumentLoader Loader() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind()], [new ProbeMappingKind()]);

    // ---- The loader -------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_RegisteredFlowType_DispatchesToTheKind_AndStampsTheEnvelope()
    {
        var document = Assert.IsType<ProbeFlowDocument>(Loader().Parse(ProbeFlow, "flows/wells.yaml"));

        Assert.Equal("wells", document.Name);
        Assert.Equal("probe", document.Kind);
        Assert.Equal("subsurface", document.Batch);
        Assert.Equal("s3://drops/wells/", document.SourceReference);
        Assert.NotNull(document.Schedule);
        Assert.Equal("0 6 * * *", document.Schedule!.Cron);
        Assert.Equal("Europe/Oslo", document.Schedule.Timezone);
        Assert.Equal(ExecutionMode.Manual, document.Mode);
    }

    [Fact]
    public void Parse_RegisteredFlowType_StampsTheDeclaredLifecycle_AndDefaultsToProduction()
    {
        var development = Loader().Parse(ProbeFlow + "\nlifecycle: development\n", "flows/wells.yaml");
        Assert.Equal(FlowLifecycle.Development, Assert.IsType<ProbeFlowDocument>(development).Lifecycle);
        Assert.Equal(FlowLifecycle.Development, Assert.Single(FlowDocumentHeaders.Project(development)).Lifecycle);

        Assert.Equal(FlowLifecycle.Production, Loader().Parse(ProbeFlow, "flows/wells.yaml") is RegisteredFlowDocument d ? d.Lifecycle : default);
    }

    [Fact]
    public void Parse_RegisteredFlowType_RefusesAnUnknownLifecycle()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(ProbeFlow + "\nlifecycle: retired\n", "flows/wells.yaml"));
        Assert.Contains("'lifecycle' has unknown value 'retired'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FlowTypeMatchesCaseInsensitively()
    {
        var document = Loader().Parse(ProbeFlow.Replace("flowType: probe", "flowType: PROBE", StringComparison.Ordinal));
        Assert.IsType<ProbeFlowDocument>(document);
    }

    [Fact]
    public void Parse_BuiltInKinds_AreUnaffectedByARegistration()
    {
        var document = Loader().Parse("""
            name: orders
            source:
              type: csv
              location: ./orders.csv
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: dbo
              table: Orders
            """);
        Assert.IsType<FileFlowDocument>(document);
    }

    [Fact]
    public void Parse_UnknownFlowType_NamesTheRegisteredKinds()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("flowType: nope\nname: x\n", "flows/x.yaml"));
        Assert.Contains("unknown flowType 'nope'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'probe' for a test-only flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownFlowType_WithoutRegistrations_KeepsTheBuiltInMessage()
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlDocumentLoader.CreateDefault().Parse("flowType: probe\nname: x\n"));
        Assert.DoesNotContain("Registered kinds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AKindReportingAnotherKind_IsRefused()
    {
        var loader = YamlDocumentLoader.CreateDefault([new ProbeFlowKind(reportedKind: "other")]);
        var ex = Assert.Throws<FlowValidationException>(() => loader.Parse(ProbeFlow, "flows/wells.yaml"));
        Assert.Contains("returned a document of kind 'other'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TheKindsOwnValidation_SurfacesAsAFlowValidationError()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("flowType: probe\nname: wells\n", "flows/wells.yaml"));
        Assert.Contains("requires a 'source'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ACompanionDocument_IsRefusedAsAFlow_NamingItsDocumentType()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse(ProbeMapping, "mappings/wells.yaml"));
        Assert.Contains("is a 'probe-mapping' document, not a flow document", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ing")]
    [InlineData("FILE")]
    [InlineData("batch")]
    [InlineData("sch")]
    public void Construction_RefusesAKindClaimingABuiltInFlowType(string flowType)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind(flowType)]));
        Assert.Contains("built-in flowType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_RefusesTwoKindsSharingAFlowType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => YamlDocumentLoader.CreateDefault([new ProbeFlowKind("probe"), new ProbeFlowKind("PROBE")]));
        Assert.Contains("registered by both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_RefusesABlankFlowType()
    {
        Assert.Throws<InvalidOperationException>(() => YamlDocumentLoader.CreateDefault([new ProbeFlowKind(" ")]));
    }

    [Fact]
    public void Construction_RefusesTwoCompanionsSharingADocumentType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => YamlDocumentLoader.CreateDefault(companions: [new ProbeMappingKind(), new ProbeMappingKind()]));
        Assert.Contains("registered by both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsKnownFlowType_CoversBuiltInAndRegisteredKinds()
    {
        var loader = Loader();
        Assert.True(loader.IsKnownFlowType("ing"));
        Assert.True(loader.IsKnownFlowType(" Probe "));
        Assert.False(loader.IsKnownFlowType("nope"));
        Assert.False(loader.IsKnownFlowType(null));
        Assert.Single(loader.Kinds);
        Assert.Single(loader.CompanionKinds);
    }

    // ---- Companion documents ----------------------------------------------------------------------------------------

    [Fact]
    public void ParseCompanion_ReturnsTheDocumentTypeAndName()
    {
        var companion = Loader().ParseCompanion(ProbeMapping, "mappings/wells.yaml");
        Assert.Equal(("probe-mapping", "wells-mapping"), companion);
    }

    [Fact]
    public void ParseCompanion_AFlowDocument_IsNotACompanion()
    {
        Assert.Null(Loader().ParseCompanion(ProbeFlow));
    }

    [Fact]
    public void ParseCompanion_AnUnknownDocumentType_NamesTheRegisteredTypes()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().ParseCompanion("documentType: nope\nname: x\n", "m.yaml"));
        Assert.Contains("unknown documentType 'nope'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'probe-mapping'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCompanion_TheCompanionsOwnValidation_Surfaces()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().ParseCompanion("documentType: probe-mapping\n", "m.yaml"));
        Assert.Contains("requires a 'name'", ex.Message, StringComparison.Ordinal);
    }

    // ---- Projection: headers and declared endpoints -----------------------------------------------------------------

    [Fact]
    public void Headers_ProjectTheDocumentsOwnHeaderMembers()
    {
        var document = Loader().Parse(ProbeFlow, "flows/wells.yaml");

        var header = Assert.Single(FlowDocumentHeaders.Project(document));
        Assert.Equal("wells", header.Name);
        Assert.Equal("probe", header.Kind);
        Assert.Equal("subsurface", header.Batch);
        Assert.Equal(ServerIdentity.From("${env:SQLFLOW_CONN_WELLS}"), header.SourceServerRef);
        Assert.Equal(ServerIdentity.FileSystem, header.TargetServerRef);
        Assert.Equal("0 6 * * *", header.Schedule!.Cron);
        Assert.Equal(ExecutionMode.Manual, header.Mode);
        Assert.True(header.ParticipatesInLineage);
    }

    [Fact]
    public void Headers_AKindWithoutAServerSide_HasNoSourceServer()
    {
        var document = Loader().Parse("flowType: probe\nname: plain\nsource: ./drops\n");

        var header = Assert.Single(FlowDocumentHeaders.Project(document));
        Assert.Null(header.SourceServerRef);
        Assert.Equal(ServerIdentity.FileSystem, header.TargetServerRef);
        Assert.Equal(ExecutionMode.Auto, header.Mode);
    }

    [Fact]
    public void DeclaredEndpoints_AreTheDocumentsSourceAndTarget()
    {
        var endpoints = FlowDeclaredEndpoints.Describe(Loader().Parse(ProbeFlow)).Select(e => e.ToString()).ToList();
        Assert.Equal(["source: s3://drops/wells/", "target: https://platform.example/api/storage/v2/records"], endpoints);
    }

    // ---- The estate scan and discovery ------------------------------------------------------------------------------

    [Fact]
    public void Collector_ScansRegisteredFlows_AndIgnoresCompanionDocuments()
    {
        WriteEstate();

        var result = new FlowSetCollector(Loader()).Collect(_dir);

        var flow = Assert.Single(result.Flows);
        Assert.Equal("wells", flow.Node.Name);
        Assert.Equal("probe", flow.Node.Kind);
        Assert.Equal("flows/wells.yaml", flow.Node.File);
        Assert.Equal("subsurface", flow.Node.Batch);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_WithTheBuiltInLoader_SkipsTheRegisteredFlowAsANonFlowFile()
    {
        WriteEstate();

        var result = new FlowSetCollector().Collect(_dir);

        Assert.Empty(result.Flows);
    }

    [Fact]
    public void Discovery_ListsRegisteredFlowsExactlyAsTheScanFindsThem()
    {
        WriteEstate();

        var discovered = Assert.Single(FlowDiscovery.Discover(_dir, Loader()));
        Assert.Equal("flows/wells.yaml", discovered.RelativePath);
        Assert.Equal("wells", discovered.FlowName);
        Assert.Equal("probe", discovered.Kind);
        Assert.True(discovered.ParseOk);
    }

    // ---- Loading for execution: secret hygiene ----------------------------------------------------------------------

    [Fact]
    public void Load_ACredentialReference_RaisesNoWarning()
    {
        var file = Write("flows/wells.yaml", ProbeFlow);
        var warnings = new List<string>();

        Assert.IsType<ProbeFlowDocument>(DocumentLoader.Load(Loader(), file, warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_AnEmbeddedCredential_WarnsNamingTheAlias_WithoutEchoingTheValue()
    {
        var file = Write("flows/wells.yaml", ProbeFlow.Replace(
            "platform: ${env:PLATFORM_TOKEN}", "warehouse: \"Server=db;Password=${env:DB_PASSWORD}\"", StringComparison.Ordinal));
        var warnings = new List<string>();

        DocumentLoader.Load(Loader(), file, warnings.Add);

        var warning = Assert.Single(warnings);
        Assert.Contains("connection 'warehouse' embeds a credential", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=db", warning, StringComparison.Ordinal);
    }

    // ---- Execution --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Execute_DispatchesToTheExecutorThatClaimsTheDocument()
    {
        var declining = new RecordingExecutor(claims: false);
        var claiming = new RecordingExecutor(claims: true);
        using var provider = new ServiceCollection()
            .AddSingleton(Loader())
            .AddSingleton<IFlowDocumentExecutor>(declining)
            .AddSingleton<IFlowDocumentExecutor>(claiming)
            .BuildServiceProvider();
        var document = Loader().Parse(ProbeFlow, "flows/wells.yaml");

        var result = await new DocumentExecutor(provider).ExecuteAsync(document, "flows/wells.yaml", new DocumentExecutionOptions());

        Assert.True(result.Success);
        Assert.Equal("wells", result.FlowName);
        Assert.Equal("probe", result.FlowKind);
        Assert.Empty(declining.Ran);
        Assert.Equal(["wells"], claiming.Ran);
    }

    [Fact]
    public async Task Execute_WithNoExecutorForTheKind_FailsNamingTheKind()
    {
        using var provider = new ServiceCollection().AddSingleton(Loader()).BuildServiceProvider();
        var document = Loader().Parse(ProbeFlow, "flows/wells.yaml");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => new DocumentExecutor(provider).ExecuteAsync(document, "flows/wells.yaml", new DocumentExecutionOptions()));
        Assert.Contains("No executor is registered for flowType 'probe'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ANameTheDocumentDoesNotDeclare_IsRefusedBeforeAnyExecutorRuns()
    {
        var claiming = new RecordingExecutor(claims: true);
        using var provider = new ServiceCollection()
            .AddSingleton(Loader())
            .AddSingleton<IFlowDocumentExecutor>(claiming)
            .BuildServiceProvider();
        var document = Loader().Parse(ProbeFlow, "flows/wells.yaml");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => new DocumentExecutor(provider).ExecuteAsync(
                document, "flows/wells.yaml", new DocumentExecutionOptions { FlowName = "wellbores" }));
        Assert.Contains("declares flow 'wells', not 'wellbores'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(claiming.Ran);
    }

    private void WriteEstate()
    {
        Write("flows/wells.yaml", ProbeFlow);
        Write("mappings/wells.yaml", ProbeMapping);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class RecordingExecutor(bool claims) : IFlowDocumentExecutor
    {
        public List<string> Ran { get; } = [];

        public bool CanExecute(RegisteredFlowDocument document) => claims && document is ProbeFlowDocument;

        public Task<DocumentExecutionResult> ExecuteAsync(
            RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
        {
            Ran.Add(document.Name);
            return Task.FromResult(new DocumentExecutionResult
            {
                FlowName = document.Name,
                FlowKind = document.Kind,
                Success = true,
                Result = document,
            });
        }
    }
}

/// <summary>A test-only flow kind (<c>flowType: probe</c>) carrying every header member the platform reads.</summary>
internal sealed class ProbeFlowKind(string flowType = "probe", string? reportedKind = null) : IFlowDocumentKind
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class Body
    {
        public string? Name { get; set; }

        public string? Batch { get; set; }

        public string? Source { get; set; }

        public string? SourceConnection { get; set; }

        public string? Target { get; set; }

        public Dictionary<string, string>? Credentials { get; set; }

        public bool RepoTree { get; set; }

        public List<ObjectBody>? Reads { get; set; }

        public List<ObjectBody>? Writes { get; set; }

        public List<ObjectBody>? Requires { get; set; }

        public List<FileBody>? Files { get; set; }

        public List<DatasetBody>? Datasets { get; set; }

        public List<DerivationBody>? Derivations { get; set; }

        public bool EstateEcho { get; set; }

        public List<string>? LineageWarnings { get; set; }

        public string? LineageFailure { get; set; }

        public string? Companion { get; set; }
    }

    private sealed class ObjectBody
    {
        public string? Connection { get; set; }

        public string? Object { get; set; }
    }

    private sealed class FileBody
    {
        public string? Relation { get; set; }

        public string? Location { get; set; }

        public string? Pattern { get; set; }
    }

    private sealed class DatasetBody
    {
        public string? Relation { get; set; }

        public string? System { get; set; }

        public string? Instance { get; set; }

        public string? Namespace { get; set; }

        public string? Group { get; set; }

        public string? Name { get; set; }

        public string? Separator { get; set; }
    }

    private sealed class DerivationBody
    {
        public DatasetBody? Dataset { get; set; }

        public List<DatasetBody>? From { get; set; }
    }

    public string FlowType => flowType;

    public string Description => "a test-only flow";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new("load", "Load", "Loads the flow's source.", WritesTarget: true),
        new("check", "Check", "Reads the target back and compares it.", WritesTarget: false),
    ];

    /// <summary>Refuses a <c>check</c> that carries a payload and any payload without a <c>scope</c> member, so the
    /// tests can see the kind's own refusal surface.</summary>
    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Payload is null)
        {
            return;
        }

        if (parameters.Operation == "check")
        {
            throw new SqlFlowException("a check takes no payload.");
        }

        using var payload = System.Text.Json.JsonDocument.Parse(parameters.Payload);
        if (!payload.RootElement.TryGetProperty("scope", out _))
        {
            throw new SqlFlowException("payload must name a 'scope'.");
        }
    }

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

        if (string.IsNullOrWhiteSpace(body.Source))
        {
            throw new FlowValidationException($"{source}: probe flow '{body.Name}' requires a 'source'.");
        }

        return new ProbeFlowDocument
        {
            FlowName = body.Name ?? string.Empty,
            ReportedKind = reportedKind ?? flowType,
            BatchLabel = body.Batch,
            Source = body.Source,
            SourceConnection = body.SourceConnection,
            Target = body.Target,
            Credentials = body.Credentials ?? [],
            RepoTree = body.RepoTree,
            Objects =
            [
                .. Declared(SqlFlow.Core.Lineage.LineageRelation.Reads, body.Reads),
                .. Declared(SqlFlow.Core.Lineage.LineageRelation.Writes, body.Writes),
                .. Declared(SqlFlow.Core.Lineage.LineageRelation.Requires, body.Requires),
            ],
            Files = (body.Files ?? []).Select(f => new DeclaredFileLocation
            {
                Relation = Relation(f.Relation),
                Location = f.Location ?? string.Empty,
                FilePattern = f.Pattern,
            }).ToList(),
            Datasets = (body.Datasets ?? []).Select(Declared).ToList(),
            Derivations = (body.Derivations ?? []).Select(d => new DeclaredDerivation
            {
                // A derivation written without its dataset reaches the platform as it is, so its refusal is what the tests see.
                Dataset = d.Dataset is null ? null! : Declared(d.Dataset),
                From = (d.From ?? []).Select(Declared).ToList(),
            }).ToList(),
            EstateEcho = body.EstateEcho,
            LineageWarnings = body.LineageWarnings ?? [],
            LineageFailure = body.LineageFailure,
            Companion = body.Companion,
        };
    }

    /// <summary>A dataset declaration as written, passed to the platform unchecked so its own checks are what the tests see.</summary>
    private static DeclaredDataset Declared(DatasetBody dataset) => new()
    {
        Relation = Relation(dataset.Relation),
        System = dataset.System ?? string.Empty,
        Instance = dataset.Instance,
        Namespace = dataset.Namespace ?? string.Empty,
        Group = dataset.Group ?? string.Empty,
        Name = dataset.Name ?? string.Empty,
        Separator = string.IsNullOrEmpty(dataset.Separator) ? null : dataset.Separator[0],
    };

    /// <summary>A declared relation as written (<c>reads</c>, <c>writes</c>, <c>requires</c>), so the platform's own
    /// refusal of a relation other than reads and writes is what the tests see.</summary>
    private static SqlFlow.Core.Lineage.LineageRelation Relation(string? value)
        => Enum.Parse<SqlFlow.Core.Lineage.LineageRelation>(value ?? "reads", ignoreCase: true);

    /// <summary>The lineage declarations of one YAML list, passed to the platform as written so its own checks are
    /// what the tests see.</summary>
    private static IEnumerable<DeclaredDataObject> Declared(SqlFlow.Core.Lineage.LineageRelation relation, List<ObjectBody>? objects)
        => (objects ?? []).Select(o => DeclaredDataObject.FromQualifiedName(relation, o.Connection ?? string.Empty, o.Object ?? string.Empty));
}

/// <summary>The document <see cref="ProbeFlowKind"/> produces.</summary>
internal sealed record ProbeFlowDocument : RegisteredFlowDocument
{
    public required string FlowName { get; init; }

    public required string ReportedKind { get; init; }

    public string? BatchLabel { get; init; }

    public required string Source { get; init; }

    public string? SourceConnection { get; init; }

    public string? Target { get; init; }

    public IReadOnlyDictionary<string, string> Credentials { get; init; } = new Dictionary<string, string>();

    public bool RepoTree { get; init; }

    public IReadOnlyList<DeclaredDataObject> Objects { get; init; } = [];

    public IReadOnlyList<DeclaredFileLocation> Files { get; init; } = [];

    public IReadOnlyList<DeclaredDataset> Datasets { get; init; } = [];

    public IReadOnlyList<DeclaredDerivation> Derivations { get; init; } = [];

    /// <summary>When set, describing the lineage warns with the registered documents the scan handed it, in the order
    /// it handed them: each as its file name and the flow it declares.</summary>
    public bool EstateEcho { get; init; }

    public IReadOnlyList<string> LineageWarnings { get; init; } = [];

    /// <summary>When set, describing the lineage throws an <see cref="InvalidOperationException"/> with this message.</summary>
    public string? LineageFailure { get; init; }

    /// <summary>A file, relative to the document's folder, listing one dataset name per line that the flow writes to the
    /// <c>probe-store</c> system; a file outside the estate is not read and is reported as a warning.</summary>
    public string? Companion { get; init; }

    public override IReadOnlyList<DeclaredDataObject> DeclaredObjects => Objects;

    /// <summary>The declared objects through the default, plus the files, datasets, derivations and warnings the
    /// document names, plus what its companion lists.</summary>
    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        var lineage = base.DescribeLineage(context);
        if (LineageFailure is not null)
        {
            throw new InvalidOperationException(LineageFailure);
        }

        var datasets = Datasets.ToList();
        var warnings = LineageWarnings.ToList();
        if (EstateEcho)
        {
            warnings.Add("the estate holds " + string.Join(", ", context.Estate.Select(entry => $"{Path.GetFileName(entry.Path)}={entry.Document.Name}")) + ".");
        }

        if (Companion is not null)
        {
            var path = Path.Combine(context.DocumentFolder, Companion);
            if (!context.Contains(path))
            {
                warnings.Add($"the companion '{Companion}' lies outside the estate and was not read.");
            }
            else
            {
                datasets.AddRange(File.ReadAllLines(path).Where(line => line.Trim().Length > 0).Select(line => new DeclaredDataset
                {
                    Relation = SqlFlow.Core.Lineage.LineageRelation.Writes,
                    System = "probe-store",
                    Namespace = "tenant",
                    Group = "companion",
                    Name = line.Trim(),
                }));
            }
        }

        return lineage with { Files = Files, Datasets = datasets, Derivations = Derivations, Warnings = warnings };
    }

    public override string Name => FlowName;

    public override string Kind => ReportedKind;

    public override string? Batch => BatchLabel;

    public override string? SourceConnectionReference => SourceConnection;

    public override string? SourceReference => Source;

    public override string? TargetReference => Target;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Credentials;

    public override bool RequiresRepoTree => RepoTree;
}

/// <summary>A test-only companion document type (<c>documentType: probe-mapping</c>).</summary>
internal sealed class ProbeMappingKind : ICompanionDocumentKind
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class Body
    {
        public string? Name { get; set; }
    }

    public string DocumentType => "probe-mapping";

    public string Description => "a test-only mapping";

    public string ParseCompanion(string yaml, string source)
    {
        var name = (Deserializer.Deserialize<Body>(yaml) ?? new Body()).Name;
        return string.IsNullOrWhiteSpace(name)
            ? throw new FlowValidationException($"{source}: a probe mapping requires a 'name'.")
            : name.Trim();
    }
}
