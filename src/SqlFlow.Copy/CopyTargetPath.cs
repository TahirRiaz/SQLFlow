using SqlFlow.Core;

namespace SqlFlow.Copy;

/// <summary>
/// The check an object-store target (Azure Blob, S3) applies to a path relative to its root before naming an object with
/// it. The path comes from a source listing or, when unzipping, from an archive entry's own name, which the archive's
/// author chooses. An object name is a flat string to the store, but the client sends it inside a request URL, and an
/// HTTP client resolves a <c>..</c> segment there, so such a name would write beside the target prefix, in another
/// folder or another container. The local endpoint resolves the path on disk and refuses one that leaves its root.
/// </summary>
internal static class CopyTargetPath
{
    /// <summary>Returns <paramref name="relativePath"/> unchanged, or refuses it when any of its segments, split on
    /// <c>/</c> or <c>\</c>, is <c>..</c>.</summary>
    public static string RequireContained(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment == "..")
            {
                throw new SqlFlowException(
                    $"Copy target path '{relativePath}' escapes the target root: its '..' segment would name an object outside it.");
            }
        }

        return relativePath;
    }
}
