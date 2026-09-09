using System.Text;
using SqlFlow.Core.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Runs every sample pipeline under <c>samples/csv</c> against the physical sink database. This both
/// proves the documented YAML examples actually work and populates a broad set of tables (one per CSV
/// use case) that can be inspected in SSMS. Each sample's table is dropped up front for a fresh start
/// and intentionally LEFT BEHIND afterwards. The two samples whose data cannot be hand-written
/// (control characters, UTF-16 encoding) have their data files generated here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SampleFlowIntegrationTests
{
    static SampleFlowIntegrationTests()
    {
        var data = Path.Combine(SamplesDir(), "data");
        Directory.CreateDirectory(data);

        // Control characters cannot be shown in a readable committed file, so generate the sample with
        // real control bytes (U+0001, U+0002) embedded between letters; stripControlChars removes them.
        var withControls = "Id,Text\n1,a" + (char)0x01 + "b" + (char)0x02 + "c\n2,clean\n";
        File.WriteAllText(Path.Combine(data, "control-chars.csv"), withControls);

        // A genuinely UTF-16 (Unicode) encoded file, with non-ASCII content to make the encoding matter.
        File.WriteAllText(Path.Combine(data, "encoding-utf16.csv"), "Id,Name\n1,Ærø\n2,Москва\n", Encoding.Unicode);
    }

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

        // The samples use paths relative to the samples folder; resolve them to absolute so the test
        // can run from anywhere.
        if (flow.Source.Location is { } location && !Path.IsPathRooted(location))
        {
            flow = flow with { Source = flow.Source with { Location = Path.GetFullPath(Path.Combine(dir, location)) } };
        }

        // The samples land in the schema they declare (most use dbo, the landing samples use the canonical
        // staging schema pre), so the checks qualify with the flow's own schema and the schema is created on
        // the sink first: the file engine creates tables, not schemas.
        var qualified = $"[{flow.Target.Schema}].[{flow.Target.Table}]";
        await IntegrationDb.ExecuteAsync(cs, $"IF SCHEMA_ID(N'{flow.Target.Schema}') IS NULL EXEC(N'CREATE SCHEMA [{flow.Target.Schema}]');");

        // Fresh start, but leave the table afterwards for inspection.
        await IntegrationDb.ExecuteAsync(cs, $"DROP TABLE IF EXISTS {qualified};");

        var result = await IntegrationDb.RealRunner().RunAsync(flow);

        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.True(
            await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT 1 WHERE OBJECT_ID('{qualified}','U') IS NOT NULL") == 1,
            $"{qualified} should exist");
        Assert.True(result.RowsLoaded > 0, "sample should load at least one row");
        Assert.Equal(result.RowsLoaded, await IntegrationDb.ScalarAsync<long>(cs, $"SELECT COUNT_BIG(*) FROM {qualified}"));
    }

    private static string SamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "csv");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the 'samples/csv' directory from the test output path.");
    }
}
