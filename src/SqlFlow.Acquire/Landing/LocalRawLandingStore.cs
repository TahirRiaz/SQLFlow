using SqlFlow.Core;

namespace SqlFlow.Acquire.Landing;

/// <summary>Lands raw payloads to the local or a UNC filesystem: the on-prem / dev sink and the test sink.</summary>
public sealed class LocalRawLandingStore : IRawLandingStore
{
    public bool CanHandle(string location)
        => !string.IsNullOrWhiteSpace(location) && location.IndexOf("://", StringComparison.Ordinal) < 0;

    public string Combine(string baseLocation, string relativePath)
        => Path.GetFullPath(Path.Combine(baseLocation, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public Task<bool> ExistsAsync(string location, CancellationToken ct = default)
        => Task.FromResult(File.Exists(location));

    public async Task<bool> PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged = true, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!overwrite)
        {
            try
            {
                await using var stream = new FileStream(location, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                return true;
            }
            catch (IOException ex) when (File.Exists(location))
            {
                throw new SqlFlowException($"Landing file '{location}' already exists and overwrite is disabled.", ex);
            }
        }

        if (!skipUnchanged)
        {
            await WriteAllAsync(location, content, ct).ConfigureAwait(false);
            return true;
        }

        // Overwriting: skip the write when the existing file is byte-identical, so an unchanged re-land does not bump
        // its last-write time and re-trigger the downstream file flow.
        return await ConditionalWrite.WriteIfChangedAsync(
            content,
            token => ContentHash.OfFileAsync(location, token),
            (_, token) => WriteAllAsync(location, content, token),
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(string baseLocation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseLocation);
        var root = Path.GetFullPath(baseLocation);
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            names.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
        }

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    private static async Task WriteAllAsync(string location, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        await using var stream = new FileStream(location, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
        await stream.WriteAsync(content, ct).ConfigureAwait(false);
    }
}
