using System.IO.Compression;
using SqlFlow.Core;
using SqlFlow.Core.Export;

namespace SqlFlow.SqlServer.Export;

/// <summary>The local/UNC filesystem export destination: handles plain paths and the <c>file://</c> scheme,
/// creating parent folders on write. The default destination when no cloud scheme is present.</summary>
public sealed class LocalExportDestination : IExportDestination
{
    public bool CanHandle(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        return location.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || !location.Contains("://", StringComparison.Ordinal);
    }

    public Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
    {
        var path = Normalize(location);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Stream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteIfExistsAsync(string location, CancellationToken ct = default)
    {
        var path = Normalize(location);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<long> GetSizeAsync(string location, CancellationToken ct = default)
    {
        var path = Normalize(location);
        return Task.FromResult(File.Exists(path) ? new FileInfo(path).Length : 0);
    }

    public Task<Stream> OpenReadAsync(string location, CancellationToken ct = default)
    {
        var path = Normalize(location);
        if (!File.Exists(path))
        {
            throw new SqlFlowException($"Cannot read '{path}': the file does not exist.");
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task<string> ZipAsync(string location, CancellationToken ct = default)
    {
        var path = Normalize(location);
        if (!File.Exists(path))
        {
            throw new SqlFlowException($"Cannot zip '{path}': the file does not exist.");
        }

        var zipPath = Path.ChangeExtension(path, ".zip");
        // Overwrite a stale archive from a prior run so the result is deterministic.
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
        }

        File.Delete(path);
        return Task.FromResult(zipPath);
    }

    private static string Normalize(string location)
        => location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(location).LocalPath : location;
}
