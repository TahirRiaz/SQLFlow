namespace SqlFlow.Core.Model;

/// <summary>
/// A storage-agnostic reference to a file - the modern equivalent of the original GenericFileItem.
/// Works for local/network paths and cloud object stores alike.
/// </summary>
public sealed record FileRef
{
    /// <summary>Full path or URI, interpreted by the owning <see cref="Abstractions.IFileStore"/>.</summary>
    public required string Path { get; init; }

    /// <summary>File name (no directory).</summary>
    public required string Name { get; init; }

    public long Size { get; init; }

    public DateTimeOffset? Modified { get; init; }
}
