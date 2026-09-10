using SqlFlow.Core.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Runs every sample pipeline under <c>samples/xml</c> against the physical sink database, proving the
/// documented XML examples work and leaving one table per use case behind for inspection. Each table is
/// dropped up front for a fresh start.
/// </summary>
[Trait("Category", "Integration")]
public sealed class XmlSampleFlowIntegrationTests
{
    public static IEnumerable<object[]> Samples()
        => Directory.EnumerateFiles(SamplesDir(), "*.flow.yaml")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { Path.GetFileName(p) });

    [SkippableTheory]
    [MemberData(nameof(Samples))]
    public async Task Sample_LoadsAndLeavesTable(string fileName)
    {
        var cs = IntegrationDb.Require();
        var dir = SamplesDir();

        var flow = new YamlFlowLoader().LoadFile(Path.Combine(dir, fileName));

        if (flow.Source.Location is { } location && !Path.IsPathRooted(location))
        {
            flow = flow with { Source = flow.Source with { Location = Path.GetFullPath(Path.Combine(dir, location)) } };
        }

        await IntegrationDb.DropTableAsync(cs, flow.Target.Table);

        var result = await IntegrationDb.RealRunner().RunAsync(flow);

        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.True(await IntegrationDb.TableExistsAsync(cs, flow.Target.Table), $"{flow.Target.Table} should exist");
        Assert.True(result.RowsLoaded > 0, "sample should load at least one row");
        Assert.Equal(result.RowsLoaded, await IntegrationDb.RowCountAsync(cs, flow.Target.Table));
    }

    private static string SamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "xml");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the 'samples/xml' directory from the test output path.");
    }
}
