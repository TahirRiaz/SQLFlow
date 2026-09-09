using System.Security.Cryptography;

namespace SqlFlow.Core;

/// <summary>
/// Content fingerprints for the engines' skip-if-unchanged writes (copy, acquire landing, export): a target that
/// already holds byte-identical content is not rewritten, so its last-modified time is not bumped and the downstream
/// file flow is not re-triggered. MD5 is used deliberately - it is the algorithm Azure Storage records as a blob's
/// <c>ContentHash</c>, so an in-memory payload compares like-for-like against what the lake already stores. It is a
/// fingerprint here, never a security primitive.
/// </summary>
public static class ContentHash
{
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
    /// <summary>The MD5 of an in-memory payload.</summary>
    public static byte[] Md5(ReadOnlySpan<byte> content) => MD5.HashData(content);

    /// <summary>The MD5 of a stream, read to its end.</summary>
    public static async Task<byte[]> Md5Async(Stream stream, CancellationToken ct)
    {
        using var md5 = MD5.Create();
        return await md5.ComputeHashAsync(stream, ct).ConfigureAwait(false);
    }
#pragma warning restore CA5351

    /// <summary>The MD5 of an existing local file, or <c>null</c> when the file does not exist.</summary>
    public static async Task<byte[]?> OfFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        return await Md5Async(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Whether two hashes are both present and byte-equal. A null or empty hash never matches, so a target
    /// whose hash is unknown is always treated as different (it gets written, which then records a hash).</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.Length > 0 && a.SequenceEqual(b);
}
