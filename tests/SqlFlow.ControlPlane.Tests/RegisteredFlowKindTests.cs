using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Node;
using SqlFlow.SourceControl.Proposals;
using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A flow kind and a companion document type a host registered, as the control plane sees them: the proposal
/// preflight judges them with the host's loader (so "preflight passed" still means "the sync will import this"), and
/// the worker's snapshot-executability decision asks the document itself. Pure parsing, no database or git.
/// </summary>
public sealed class RegisteredFlowKindTests
{
    private const string DropFlow = """
        flowType: drop
        name: wells
        source: s3://drops/wells/
        target: https://platform.example/api/storage/v2/records
        """;

    private static readonly YamlDocumentLoader Loader =
        YamlDocumentLoader.CreateDefault([new DropFlowKind()], [new DropMappingKind()]);

    [Fact]
    public void Preflight_ARegisteredKindsFlow_PassesWithTheHostLoader()
    {
        var result = FlowProposalPreflight.Run([new ProposalFile("flows/wells.yaml", DropFlow)], [], Loader);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Preflight_ARegisteredKindsFlow_IsAnErrorForAHostThatDoesNotRegisterIt()
    {
        var result = FlowProposalPreflight.Run([new ProposalFile("flows/wells.yaml", DropFlow)], []);

        var error = Assert.Single(result.Errors);
        Assert.Contains("unknown flowType 'drop'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_AValidCompanionDocument_Passes()
    {
        var result = FlowProposalPreflight.Run(
            [new ProposalFile("mappings/wells.yaml", "documentType: drop-mapping\nname: wells-mapping\n")], [], Loader);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Preflight_ACompanionDocumentItsKindRefuses_IsAnError()
    {
        var result = FlowProposalPreflight.Run(
            [new ProposalFile("mappings/wells.yaml", "documentType: drop-mapping\n")], [], Loader);

        var error = Assert.Single(result.Errors);
        Assert.Equal("mappings/wells.yaml", error.Path);
        Assert.Contains("does not parse as a companion document", error.Message, StringComparison.Ordinal);
        Assert.Contains("requires a 'name'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_AnUnregisteredDocumentType_IsAnError()
    {
        var result = FlowProposalPreflight.Run(
            [new ProposalFile("mappings/wells.yaml", "documentType: nope\nname: x\n")], [], Loader);

        var error = Assert.Single(result.Errors);
        Assert.Contains("unknown documentType 'nope'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_ARevisionThatRepointsARegisteredFlow_WarnsWithTheEndpointDiff()
    {
        var existing = new FlowProposalPreflight.ExistingPipeline("wells", "flows/wells.yaml", DropFlow);
        var revised = DropFlow.Replace("s3://drops/wells/", "s3://drops/elsewhere/", StringComparison.Ordinal);

        var result = FlowProposalPreflight.Run([new ProposalFile("flows/wells.yaml", revised)], [existing], Loader);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("CHANGES its declared endpoints", warning.Message, StringComparison.Ordinal);
        Assert.Contains("removed [source: s3://drops/wells/]", warning.Message, StringComparison.Ordinal);
        Assert.Contains("added [source: s3://drops/elsewhere/]", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_ARevisionThatKeepsTheEndpoints_PassesClean()
    {
        var existing = new FlowProposalPreflight.ExistingPipeline("wells", "flows/wells.yaml", DropFlow);
        var revised = DropFlow + "\nmode: manual\n";

        var result = FlowProposalPreflight.Run([new ProposalFile("flows/wells.yaml", revised)], [existing], Loader);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RequiresRepoTree_IsTheDocumentsOwnAnswer()
    {
        Assert.False(RunWorker.RequiresRepoTree(Loader.Parse(DropFlow)));
        Assert.True(RunWorker.RequiresRepoTree(Loader.Parse(DropFlow + "\nrepoTree: true\n")));
    }

    private sealed class DropFlowKind : IFlowDocumentKind
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private sealed class Body
        {
            public string? Name { get; set; }

            public string? Source { get; set; }

            public string? Target { get; set; }

            public bool RepoTree { get; set; }
        }

        public string FlowType => "drop";

        public string Description => "a test-only drop flow";

        public IReadOnlyList<SqlFlow.Core.Runs.FlowKindOperation> Operations { get; } =
            [new("send", "Send", "Sends the flow's records.", WritesTarget: true)];

        public void ValidateParameters(SqlFlow.Core.Runs.RunParameters parameters) => ArgumentNullException.ThrowIfNull(parameters);

        public RegisteredFlowDocument Parse(string yaml, string source)
        {
            var body = Deserializer.Deserialize<Body>(yaml) ?? new Body();
            return new DropFlowDocument
            {
                FlowName = body.Name ?? string.Empty,
                Source = body.Source,
                Target = body.Target,
                RepoTree = body.RepoTree,
            };
        }
    }

    private sealed record DropFlowDocument : RegisteredFlowDocument
    {
        public required string FlowName { get; init; }

        public string? Source { get; init; }

        public string? Target { get; init; }

        public bool RepoTree { get; init; }

        public override string Name => FlowName;

        public override string Kind => "drop";

        public override string? SourceReference => Source;

        public override string? TargetReference => Target;

        public override bool RequiresRepoTree => RepoTree;
    }

    private sealed class DropMappingKind : ICompanionDocumentKind
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private sealed class Body
        {
            public string? Name { get; set; }
        }

        public string DocumentType => "drop-mapping";

        public string Description => "a test-only mapping";

        public string ParseCompanion(string yaml, string source)
        {
            var name = (Deserializer.Deserialize<Body>(yaml) ?? new Body()).Name;
            return string.IsNullOrWhiteSpace(name)
                ? throw new FlowValidationException($"{source}: a drop mapping requires a 'name'.")
                : name.Trim();
        }
    }
}
