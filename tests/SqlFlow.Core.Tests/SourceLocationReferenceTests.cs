using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A file flow's source location that names a reference (<c>${env:LANDING_URL}/logtype</c>): the loader leaves it as
/// written instead of anchoring it to the flow's folder, and the run resolves it with its own values, so a flow reads the
/// storage its deployment configures. A resolved location is an absolute path or a URI; a location without a reference is
/// used as written.
/// </summary>
public sealed class SourceLocationReferenceTests
{
    private const string Landing = "abfss://landing@estate.dfs.core.windows.net/drops";

    private static readonly SecretResolver Node = new([new NoEnvironment()]);

    private static FlowDefinition Flow(string? location, string? srcPath = null) => new()
    {
        Name = "logtype",
        Source = new SourceSpec
        {
            Type = "csv",
            Location = location,
            Options = srcPath is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [SourceLocations.SrcPathOption] = srcPath },
        },
        Target = new TargetSpec { Connection = "${env:WAREHOUSE_DB}", Schema = "pre", Table = "LogType" },
    };

    private static IDisposable Supplied(string landing)
        => SuppliedReferences.Supply(new Dictionary<string, string>(StringComparer.Ordinal) { ["LANDING_URL"] = landing });

    [Fact]
    public async Task ALocationNamingAReference_ReadsWhereTheRunsValuesPoint()
    {
        using var scope = Supplied(Landing);

        var resolved = await SourceLocations.ResolveAsync(Flow("${env:LANDING_URL}/logtype"), Node, CancellationToken.None);

        Assert.Equal(Landing + "/logtype", resolved.Source.Location);
    }

    [Fact]
    public async Task TheSrcPathOption_IsResolvedTheSameWay()
    {
        using var scope = Supplied(Landing);

        var resolved = await SourceLocations.ResolveAsync(Flow(null, "${env:LANDING_URL}/curves"), Node, CancellationToken.None);

        Assert.Equal(Landing + "/curves", resolved.Source.Options[SourceLocations.SrcPathOption]);
    }

    [Fact]
    public async Task ALocationWithoutAReference_IsUsedAsWritten()
    {
        var flow = Flow("C:/drops/logtype");

        Assert.Same(flow, await SourceLocations.ResolveAsync(flow, Node, CancellationToken.None));
    }

    [Fact]
    public async Task AReferenceResolvingToARelativePath_IsRefused_NamingTheLocationAsWritten()
    {
        using var scope = Supplied("drops/relative");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => SourceLocations.ResolveAsync(Flow("${env:LANDING_URL}/logtype"), Node, CancellationToken.None));

        Assert.StartsWith("The source.location '${env:LANDING_URL}/logtype' resolves to a relative or empty location.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceThatDoesNotResolve_IsRefused_NamingTheLocationAsWritten()
    {
        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => SourceLocations.ResolveAsync(Flow("${env:LANDING_URL}/logtype"), Node, CancellationToken.None));

        Assert.StartsWith("The source.location '${env:LANDING_URL}/logtype' names a reference that does not resolve: Environment variable 'LANDING_URL' is not set.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoader_LeavesALocationNamingAReferenceAsWritten_AndStillAnchorsARelativeOne()
    {
        var file = Path.Combine(Path.GetTempPath(), "estate", "flows", "logtype.flow.yaml");

        Assert.Equal("${env:LANDING_URL}/logtype", DocumentLoader.ResolveRelativeLocation(Flow("${env:LANDING_URL}/logtype"), file).Source.Location);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "estate", "data", "logtype")),
            DocumentLoader.ResolveRelativeLocation(Flow("../data/logtype"), file).Source.Location);
    }

    /// <summary>A process environment that holds nothing, failing as the real one does for a variable that is not set.</summary>
    private sealed class NoEnvironment : ISecretProvider
    {
        public string Scheme => "env";

        public string Resolve(string locator) => throw new SqlFlowException($"Environment variable '{locator}' is not set.");
    }
}
