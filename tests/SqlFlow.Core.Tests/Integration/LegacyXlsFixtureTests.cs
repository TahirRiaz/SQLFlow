using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Parses real Excel files dropped into <c>tests/SqlFlow.Core.Tests/fixtures</c>. This is how the
/// legacy binary <c>.xls</c> format gets covered (ClosedXML can only author <c>.xlsx</c>, so a genuine
/// <c>.xls</c> must be a committed file). The test skips cleanly when no fixtures are present, so the
/// default suite is unaffected; it runs once a file is added.
/// </summary>
public sealed class LegacyXlsFixtureTests
{
    [SkippableFact]
    public async Task Fixtures_ParseIntoColumnsAndRows()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(IsExcel).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];

        Skip.If(files.Count == 0, "No Excel fixtures present. Drop a .xls/.xlsx into tests/SqlFlow.Core.Tests/fixtures to enable.");

        var reader = new XlsSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var type = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            var source = new SourceSpec { Type = type, Location = path, Options = new Dictionary<string, string?>() };

            var columns = await reader.GetColumnsAsync(source);

            // At least one real (non-provenance) column should be discovered.
            Assert.True(
                columns.Any(c => !c.Name.EndsWith("_DW", StringComparison.Ordinal) && c.Name != "FileLineNumber"),
                $"'{fileName}' produced no source columns.");

            var read = await reader.OpenAsync(source, columns);
            await using var data = read.Reader;

            var rows = 0;
            while (await data.ReadAsync())
            {
                rows++;
            }

            Assert.True(rows > 0, $"'{fileName}' parsed but yielded no rows.");
        }
    }

    private static bool IsExcel(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
    }
}
