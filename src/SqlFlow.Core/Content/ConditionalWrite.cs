namespace SqlFlow.Core;

/// <summary>
/// The skip-if-unchanged write shared by the file-writing engines. It hashes the payload, reads the target's current
/// content hash, and writes only when they differ - so a byte-identical rewrite (a copy re-run, an acquire fetch that
/// returned the same file, an export of unchanged data) leaves the target and its last-modified time untouched, and
/// does not re-trigger the downstream file flow. The two I/O sides are supplied by the caller so any store (Azure
/// blob, local disk) reuses the one decision.
/// </summary>
public static class ConditionalWrite
{
    /// <summary>Writes <paramref name="content"/> only if the target's current hash differs from it. Returns
    /// <c>true</c> when bytes were written, <c>false</c> when the write was skipped because the target already held
    /// byte-identical content.</summary>
    /// <param name="content">The payload to write.</param>
    /// <param name="currentTargetHash">Reads the target's stored content hash, or <c>null</c> when the target has no
    /// such object (or cannot report a hash without reading it).</param>
    /// <param name="writeStamped">Writes the payload, receiving the payload's MD5 so a store that records a content
    /// hash (Azure blob) can stamp it for the next run's comparison.</param>
    /// <param name="ct">Cancels the hash read and the write.</param>
    public static async Task<bool> WriteIfChangedAsync(
        ReadOnlyMemory<byte> content,
        Func<CancellationToken, Task<byte[]?>> currentTargetHash,
        Func<byte[], CancellationToken, Task> writeStamped,
        CancellationToken ct)
    {
        var hash = ContentHash.Md5(content.Span);
        var existing = await currentTargetHash(ct).ConfigureAwait(false);
        if (existing is not null && ContentHash.Equal(existing, hash))
        {
            return false;
        }

        await writeStamped(hash, ct).ConfigureAwait(false);
        return true;
    }
}
