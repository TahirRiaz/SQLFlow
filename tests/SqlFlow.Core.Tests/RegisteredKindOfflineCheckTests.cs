using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Tests;

/// <summary>
/// <c>sqlflow validate</c> asks a document of a registered kind what it can check offline beyond parsing
/// (<see cref="RegisteredFlowDocument.CheckOffline"/>): a companion file the document names that is not on disk where it
/// says refuses the document, in the single-file form and in the estate form alike. Parsing the document does not check
/// it, so the estate scan, the catalog sync and a node keep loading such a document as they did. The kind here is
/// test-only: its documents name one companion file in a <c>companions</c> folder beside them. The class reads what the CLI
/// writes to the process-wide console, so it runs alone (<see cref="RegisteredKindOfflineCheckConsoleGroup"/>).
/// </summary>
[Collection(RegisteredKindOfflineCheckTests.ConsoleCollection)]
public sealed class RegisteredKindOfflineCheckTests : IDisposable
{
    /// <summary>The collection that runs this class alone, after the parallel ones.</summary>
    public const string ConsoleCollection = "Registered kind offline check on the console";

    private readonly string _dir = Directory.CreateTempSubdirectory("sqlflow-kind-offline-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ValidatingAFile_WhoseCompanionIsMissing_IsRefused_NamingTheFileAndTheCompanion()
    {
        var flow = await WriteFlowAsync("parcels", "parcels.txt", companionExists: false);

        var (exit, output, errors) = await ValidateAsync(flow);

        Assert.Equal(1, exit);
        Assert.Contains($"ERROR  {flow}: companion 'parcels.txt' is not in the companions folder beside the document.", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("is valid", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatingAFile_WhoseCompanionIsThere_IsValid()
    {
        var flow = await WriteFlowAsync("parcels", "parcels.txt", companionExists: true);

        var (exit, output, _) = await ValidateAsync(flow);

        Assert.Equal(0, exit);
        Assert.Contains("OK  'parcels' is valid (companion)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatingAnEstate_ReportsTheDocumentWhoseCompanionIsMissing_AsBroken_AndTheOtherAsValid()
    {
        await WriteFlowAsync("parcels", "parcels.txt", companionExists: true);
        await WriteFlowAsync("roads", "roads.txt", companionExists: false);

        var (exit, output, _) = await ValidateAsync(_dir);

        Assert.Equal(1, exit);
        Assert.Contains("OK      parcels.yaml  (companion 'parcels')", output, StringComparison.Ordinal);
        Assert.Contains("BROKEN  roads.yaml: ", output, StringComparison.Ordinal);
        Assert.Contains("companion 'roads.txt' is not in the companions folder beside the document.", output, StringComparison.Ordinal);
        Assert.Contains("1 valid, 1 broken of 2 document(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsingTheDocument_DoesNotCheckItsCompanion_OnlyTheOfflineCheckDoes()
    {
        var flow = await WriteFlowAsync("roads", "roads.txt", companionExists: false);
        var documents = YamlDocumentLoader.CreateDefault([new CompanionFlowKind()]);

        // What the estate scan, the catalog sync and a node load: the document as it stands.
        var document = Assert.IsType<CompanionFlowDocument>(documents.LoadFile(flow));
        Assert.Equal("roads", document.Name);

        var refused = Assert.Throws<FlowValidationException>(() => DocumentLoader.CheckOffline(document, flow));
        Assert.Equal($"{flow}: companion 'roads.txt' is not in the companions folder beside the document.", refused.Message);
    }

    [Fact]
    public void TheOfflineCheck_PassesADocumentThatChecksNothing_AndABuiltInOne()
    {
        var registered = new CompanionFlowDocument("parcels", null);
        var builtIn = YamlDocumentLoader.CreateDefault().Parse(
            """
            flowType: batch
            name: nightly
            members:
              include:
                - "*.flow.yaml"
            """,
            "flows/nightly.yaml");

        DocumentLoader.CheckOffline(registered, Path.Combine(_dir, "parcels.yaml"));
        DocumentLoader.CheckOffline(builtIn, Path.Combine(_dir, "parcels.yaml"));
        Assert.Empty(new MinimalDocument().CheckOffline(Path.Combine(_dir, "parcels.yaml")));
    }

    private async Task<string> WriteFlowAsync(string name, string companion, bool companionExists)
    {
        var flow = Path.Combine(_dir, name + ".yaml");
        await File.WriteAllTextAsync(flow, $"flowType: companion\nname: {name}\ncompanion: {companion}\n");
        if (companionExists)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_dir, CompanionFlowDocument.Folder)).FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, companion), "a companion");
        }

        return flow;
    }

    /// <summary>Runs <c>sqlflow validate</c> on <paramref name="target"/> with the test kind registered, as a host module registers its own.</summary>
    private static async Task<(int Exit, string Output, string Errors)> ValidateAsync(string target)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var exit = await CliHost.RunAsync(["validate", target], new CompanionKindModule());
            return (exit, output.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class CompanionKindModule : ICliModule
    {
        public string Name => "companion";

        public IReadOnlyList<CliVerb> Verbs => [];

        public void ConfigureServices(CliModuleServices services)
            => services.Services.AddSingleton<IFlowDocumentKind>(new CompanionFlowKind());
    }

    /// <summary>A document of the test kind: it names a companion file, looked for in <see cref="Folder"/> beside it.</summary>
    private sealed record CompanionFlowDocument(string FlowName, string? Companion) : RegisteredFlowDocument
    {
        public const string Folder = "companions";

        public override string Name => FlowName;

        public override string Kind => "companion";

        public override IReadOnlyList<string> CheckOffline(string documentPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
            if (Companion is null)
            {
                return [];
            }

            var folder = Path.Combine(Path.GetDirectoryName(documentPath)!, Folder);
            return File.Exists(Path.Combine(folder, Companion))
                ? []
                : [$"companion '{Companion}' is not in the {Folder} folder beside the document."];
        }
    }

    /// <summary>A document that keeps the default offline check.</summary>
    private sealed record MinimalDocument : RegisteredFlowDocument
    {
        public override string Name => "minimal";

        public override string Kind => "minimal";
    }

    private sealed class CompanionFlowKind : IFlowDocumentKind
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public string FlowType => "companion";

        public string Description => "a test-only flow that names a companion file beside it";

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
                ? throw new FlowValidationException($"{source}: a companion flow requires a 'name'.")
                : new CompanionFlowDocument(body.Name, string.IsNullOrWhiteSpace(body.Companion) ? null : body.Companion);
        }

        public void ValidateParameters(RunParameters parameters) => ArgumentNullException.ThrowIfNull(parameters);

        private sealed class Body
        {
            public string? Name { get; set; }

            public string? Companion { get; set; }
        }
    }
}

/// <summary>Runs <see cref="RegisteredKindOfflineCheckTests"/> alone, after the parallel collections, because it redirects
/// the process-wide standard output and standard error.</summary>
[CollectionDefinition(RegisteredKindOfflineCheckTests.ConsoleCollection, DisableParallelization = true)]
public sealed class RegisteredKindOfflineCheckConsoleGroup
{
}
