using SqlFlow.Core.Acquire;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>Parses every shipped <c>samples/api/*.flow.yaml</c> through the real loader, so a broken sample (or a
/// schema regression) fails the build. This is the executable proof that the runbook patterns port to YAML.</summary>
public sealed class SamplesTests
{
    public static IEnumerable<object[]> SampleFiles()
    {
        var dir = FindSamplesDir();
        foreach (var file in Directory.EnumerateFiles(dir, "*.flow.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return [file];
        }
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Sample_parses_into_a_valid_acquire_flow(string file)
    {
        var loader = new YamlAcquireFlowLoader();
        var flow = loader.LoadFile(file);

        Assert.False(string.IsNullOrWhiteSpace(flow.Name));
        Assert.NotEmpty(flow.Items);
        foreach (var item in flow.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Landing.Target));
            Assert.False(string.IsNullOrWhiteSpace(item.Landing.PathTemplate));
            if (item.Source.Transport == AcquireTransport.Http)
            {
                Assert.NotNull(item.Source.Request);
            }
        }
    }

    [Fact]
    public void All_transports_are_represented_by_the_samples()
    {
        var transports = SampleFiles()
            .Select(a => new YamlAcquireFlowLoader().LoadFile((string)a[0]).Source.Transport)
            .Distinct()
            .ToHashSet();

        Assert.Contains(AcquireTransport.Http, transports);
        Assert.Contains(AcquireTransport.Sftp, transports);
        Assert.Contains(AcquireTransport.AzureTable, transports);
    }

    private static string FindSamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "api");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate samples/api from the test base directory.");
    }
}
