using System.IO.Compression;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.SqlServer.Export;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Coverage for the export ZipTrg compression (legacy ZipTrg): the local destination replaces a written file
/// with a single-entry <c>.zip</c>, and a destination that does not implement compression surfaces a clear
/// error via the interface default rather than silently ignoring the request.
/// </summary>
public sealed class ExportZipTests
{
    [Fact]
    public async Task LocalDestination_Zip_ReplacesFileWithSingleEntryArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfzip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "orders.csv");
        await File.WriteAllTextAsync(csv, "Id;Amount\n1;100\n2;200\n");

        try
        {
            var destination = new LocalExportDestination();
            var zipPath = await destination.ZipAsync(csv);

            Assert.Equal(Path.Combine(dir, "orders.zip"), zipPath);
            Assert.True(File.Exists(zipPath));
            Assert.False(File.Exists(csv)); // original is replaced

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("orders.csv", entry.Name);
            Assert.True(entry.Length > 0);

            // Size probe reads the archive, not the original.
            Assert.True(await destination.GetSizeAsync(zipPath) > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDestination_Zip_OverwritesStaleArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfzip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "orders.csv");
        await File.WriteAllTextAsync(csv, "data");
        await File.WriteAllTextAsync(Path.Combine(dir, "orders.zip"), "stale");

        try
        {
            var zipPath = await new LocalExportDestination().ZipAsync(csv);
            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Single(archive.Entries); // the stale non-archive file was replaced, not appended to
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDestination_Zip_MissingFile_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sfzip_" + Guid.NewGuid().ToString("N"), "nope.csv");
        await Assert.ThrowsAsync<SqlFlowException>(() => new LocalExportDestination().ZipAsync(missing));
    }

    [Fact]
    public async Task DestinationWithoutZipSupport_UsesFailLoudDefault()
    {
        IExportDestination destination = new NoZipDestination();
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => destination.ZipAsync("some/where/file.csv"));
        Assert.Contains("zipTrg", ex.Message, StringComparison.Ordinal);
    }

    // A destination that implements only the write seam and inherits the default ZipAsync (the fail-loud path a
    // future cloud destination gets for free until it implements compression).
    private sealed class NoZipDestination : IExportDestination
    {
        public bool CanHandle(string location) => true;
        public Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteIfExistsAsync(string location, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetSizeAsync(string location, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<Stream> OpenReadAsync(string location, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
    }
}
