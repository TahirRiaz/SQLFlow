namespace SqlFlow.Core.Model;

/// <summary>
/// The request a reader hands an <see cref="Abstractions.IFileStore"/> to enumerate source files: the file-name
/// glob, whether to recurse, and an optional semantic <see cref="Filter"/>. The store applies the glob itself and,
/// when a filter is present, consults it to prune whole out-of-window directories during the walk (so a lake with
/// years of partitions is never fully enumerated) and to drop individual files. A null filter means "list every
/// glob match", the plain enumeration.
/// </summary>
public sealed record FileDiscovery
{
    /// <summary>File-name glob applied at every enumerated directory level (e.g. <c>*.csv</c>).</summary>
    public required string Pattern { get; init; }

    /// <summary>Recurse into sub-directories.</summary>
    public bool Recursive { get; init; }

    /// <summary>Optional semantic filter for directory pruning and per-file inclusion; null lists every glob match.</summary>
    public IFileDiscoveryFilter? Filter { get; init; }
}

/// <summary>
/// The pruning and inclusion decisions a store consults while enumerating. Keeping both on one interface lets a
/// store skip an out-of-window partition subtree (<see cref="ShouldEnterDirectory"/>) without walking it, and
/// still apply the finer per-file test (<see cref="Includes"/>) to the files it does list. The store stays free of
/// any date or mask semantics; those live entirely in the filter implementation.
/// </summary>
public interface IFileDiscoveryFilter
{
    /// <summary>
    /// True when the store should descend into <paramref name="directoryPath"/> during a recursive walk. A filter
    /// that cannot rule the whole subtree out (e.g. the date lives in the file name, or the folder carries no date
    /// tokens yet) returns true so nothing is missed; it prunes only when the folder is provably out of window.
    /// </summary>
    bool ShouldEnterDirectory(string directoryPath);

    /// <summary>True when <paramref name="file"/> passes every file-level filter (path mask, date window).</summary>
    bool Includes(FileRef file);
}
