using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Local and network (UNC) file system store. Handles plain paths and the <c>file://</c> scheme;
/// the default store when no cloud scheme is present.
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    public bool CanHandle(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Plain local/UNC path (no URI scheme like abfss://, s3://, gs://, https://).
        return !location.Contains("://", StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var path = Normalize(location);
        var filter = discovery.Filter;

        if (File.Exists(path))
        {
            var single = ToRef(path);
            IReadOnlyList<FileRef> result = filter is null || filter.Includes(single) ? [single] : [];
            return Task.FromResult(result);
        }

        if (Directory.Exists(path))
        {
            var pattern = string.IsNullOrWhiteSpace(discovery.Pattern) ? "*" : discovery.Pattern;
            var files = new List<FileRef>();
            Walk(path, pattern, discovery.Recursive, filter, files, ct);
            files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            return Task.FromResult<IReadOnlyList<FileRef>>(files);
        }

        throw new SqlFlowException($"Path not found: '{location}'.");
    }

    public Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Task.FromResult<Stream>(File.OpenRead(file.Path));
    }

    /// <summary>
    /// Enumerates one directory level and, when recursing, descends only into sub-directories the filter allows -
    /// so an out-of-window partition subtree is skipped without ever being walked. Files at each level are matched
    /// by the glob and then passed through the filter's per-file test. A directory made inaccessible mid-walk
    /// (permissions, a concurrent delete) is skipped rather than aborting the whole listing.
    /// </summary>
    private static void Walk(string directory, string pattern, bool recursive, IFileDiscoveryFilter? filter, List<FileRef> sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<string> matched;
        try
        {
            matched = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in matched)
        {
            ct.ThrowIfCancellationRequested();
            var reference = ToRef(file);
            if (filter is null || filter.Includes(reference))
            {
                sink.Add(reference);
            }
        }

        if (!recursive)
        {
            return;
        }

        IEnumerable<string> subDirectories;
        try
        {
            subDirectories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var subDirectory in subDirectories)
        {
            if (filter is null || filter.ShouldEnterDirectory(subDirectory))
            {
                Walk(subDirectory, pattern, recursive, filter, sink, ct);
            }
        }
    }

    private static string Normalize(string location)
        => location.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(location).LocalPath
            : location;

    private static FileRef ToRef(string path)
    {
        var info = new FileInfo(path);
        return new FileRef
        {
            Path = path,
            Name = info.Name,
            Size = info.Exists ? info.Length : 0,
            Modified = info.Exists ? info.LastWriteTimeUtc : null,
        };
    }
}
