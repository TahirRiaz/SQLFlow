using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// End-to-end file-date discovery over a partitioned folder tree: the store prunes out-of-window partition folders
/// during the walk (never enumerating them), and the reader selects files by a path- or name-derived business date
/// rather than the file's modified timestamp.
/// </summary>
public sealed class FileDateDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_filedate_" + Guid.NewGuid().ToString("N"));
    private readonly CsvSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public FileDateDiscoveryTests() => Directory.CreateDirectory(_dir);

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<List<string?>> LoadIdsAsync(Dictionary<string, string?> options)
    {
        var source = new SourceSpec { Type = "csv", Location = _dir, Options = options };
        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var ids = new List<string?>();
        while (await data.ReadAsync())
        {
            ids.Add(data.IsDBNull(0) ? null : Convert.ToString(data.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    [Fact]
    public async Task HivePath_Window_SelectsOnlyInWindowPartitions()
    {
        Write(@"year=2024\month=12\old.csv", "Id\nold\n");
        Write(@"year=2025\month=06\mid.csv", "Id\nmid\n");
        Write(@"year=2026\month=01\new.csv", "Id\nnew\n");

        var ids = await LoadIdsAsync(new Dictionary<string, string?>
        {
            ["srcFile"] = "*.csv",
            ["searchSubDirectories"] = "true",
            ["fileDate.from"] = "path",
            ["fileDate.hive"] = "true",
            ["initFromFileDate"] = "2025-01-01",
            ["initToFileDate"] = "2025-12-31",
        });

        Assert.Equal(new[] { "mid" }, ids);
    }

    [Fact]
    public async Task NameDate_Window_SelectsByFileNameDate()
    {
        Write("orders_2024-12-31.csv", "Id\na\n");
        Write("orders_2025-03-01.csv", "Id\nb\n");
        Write("orders_2025-06-15.csv", "Id\nc\n");

        var ids = await LoadIdsAsync(new Dictionary<string, string?>
        {
            ["srcFile"] = "*.csv",
            ["fileDate.from"] = "name",
            ["fileDate.pattern"] = @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})",
            ["initFromFileDate"] = "2025-01-01",
        });

        Assert.Equal(new[] { "b", "c" }, ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task NameDate_UndatedFile_IsExcludedWhenWindowSet()
    {
        Write("orders_2025-03-01.csv", "Id\nb\n");
        Write("orders_nodate.csv", "Id\nx\n");

        var ids = await LoadIdsAsync(new Dictionary<string, string?>
        {
            ["srcFile"] = "*.csv",
            ["fileDate.from"] = "name",
            ["fileDate.pattern"] = @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})",
            ["initFromFileDate"] = "2025-01-01",
        });

        Assert.Equal(new[] { "b" }, ids);
    }

    [Fact]
    public async Task HivePath_IncrementalWatermark_ReadsPastTheBoundOnly()
    {
        Write(@"year=2025\month=01\jan.csv", "Id\njan\n");
        Write(@"year=2025\month=06\jun.csv", "Id\njun\n");

        var ids = await LoadIdsAsync(new Dictionary<string, string?>
        {
            ["srcFile"] = "*.csv",
            ["searchSubDirectories"] = "true",
            ["fileDate.from"] = "path",
            ["fileDate.hive"] = "true",
            ["incrementalAfterDate"] = "2025-03-01",
        });

        Assert.Equal(new[] { "jun" }, ids);
    }

    [Fact]
    public async Task Store_PrunesOutOfWindowFolders_WithoutWalkingThem()
    {
        Write(@"year=2024\month=01\a.csv", "Id\na\n");
        Write(@"year=2025\month=06\b.csv", "Id\nb\n");
        Write(@"year=2026\month=01\c.csv", "Id\nc\n");

        var spy = new SpyFilter();
        var discovery = new FileDiscovery { Pattern = "*.csv", Recursive = true, Filter = spy };
        var files = await new LocalFileStore().ListAsync(_dir, discovery);

        // Only the in-window partition yields a file, and the pruned years' month folders were never entered.
        Assert.Equal(new[] { "b.csv" }, files.Select(f => f.Name).ToArray());
        Assert.Contains(spy.Entered, d => d.Contains("year=2025", StringComparison.Ordinal));
        Assert.DoesNotContain(spy.Entered, d => d.Contains("year=2024", StringComparison.Ordinal) && d.Contains("month=", StringComparison.Ordinal));
        Assert.DoesNotContain(spy.Entered, d => d.Contains("year=2026", StringComparison.Ordinal) && d.Contains("month=", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Prunes the 2024 and 2026 partition subtrees and records every directory the store asks about, so a
    /// test can prove a pruned subtree was never descended into.</summary>
    private sealed class SpyFilter : IFileDiscoveryFilter
    {
        public List<string> Entered { get; } = [];

        public bool ShouldEnterDirectory(string directoryPath)
        {
            Entered.Add(directoryPath);
            return !directoryPath.Contains("year=2024", StringComparison.Ordinal)
                && !directoryPath.Contains("year=2026", StringComparison.Ordinal);
        }

        public bool Includes(FileRef file)
            => file.Path.Contains("year=2025", StringComparison.Ordinal);
    }
}
