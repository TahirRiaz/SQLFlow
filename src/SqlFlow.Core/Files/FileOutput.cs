namespace SqlFlow.Core.Files;

/// <summary>
/// A file (or file set) a flow declares that it produces, so lineage can connect the flow to the downstream file
/// ingestion(s) that read it. Used by every flow type whose landed output is not fully determinable from its other
/// fields: an invoke (external compute), a cpy that fans a source into several folders, or an sftp download that
/// drops many file sets. The shape mirrors a file source's selection spec: <see cref="Location"/> is the folder (or
/// a full file path) the run writes to, <see cref="SrcFile"/> is the file-name glob within it (defaulted from the
/// location's extension when absent), and <see cref="SrcPathMask"/> is an optional regex over the full path. From
/// this, <see cref="SqlFlow.Core.Files.FileSelection.Feeds"/> decides, with engine parity, which ingestion each
/// declared output feeds, so one output can fan out to every matching consumer. Immutable.
/// </summary>
public sealed record FileOutput
{
    /// <summary>The folder or full file path the run lands data at: a local path (normalized against the estate root
    /// for lineage identity), an Azure storage URI (canonicalized so the shape does not matter), or another cloud URL
    /// kept verbatim. Required whenever an output is declared.</summary>
    public required string Location { get; init; }

    /// <summary>The file-name glob the run produces within <see cref="Location"/> (e.g. <c>orders_*.csv</c>); when
    /// absent it is inferred from the location's file name, or left to match any file in the folder.</summary>
    public string? SrcFile { get; init; }

    /// <summary>An optional regex over the full landing path, matched the way a file source's <c>srcPathMask</c> is,
    /// for cases the folder prefix cannot express.</summary>
    public string? SrcPathMask { get; init; }

    /// <summary>This output as a file-selection spec, so the shared matcher treats a declared output and a file
    /// source identically.</summary>
    public FileSelectionSpec ToSelectionSpec()
        => new() { Location = Location, Glob = SrcFile, Mask = SrcPathMask };
}
