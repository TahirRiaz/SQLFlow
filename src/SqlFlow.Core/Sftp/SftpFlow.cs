using SqlFlow.Core.Files;
using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Sftp;

/// <summary>
/// A validated SFTP transfer flow (<c>flowType: sftp</c>): downloads files from an SFTP server into the data lake
/// (or a local path), or uploads files from the lake/local to the server, byte-for-byte. It is the dedicated
/// interface to SFTP, the file counterpart to the <c>api</c> (REST) flow type: one protocol, one flow type. It
/// performs no parsing; the downstream json/csv/xml file flows ingest what it lands.
/// </summary>
public sealed record SftpFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    public required SftpServer Server { get; init; }

    /// <summary>Which way files move: down from the server into each step's local target (default), or up to the
    /// server. One direction applies to the whole flow.</summary>
    public SftpDirection Direction { get; init; } = SftpDirection.Download;

    /// <summary>The transfer steps this flow performs, in order: each a remote-path/local-path pair with its own
    /// selection. One flow moves as many file sets as it declares over a single connection, so a vendor that drops
    /// several file sets is one pipeline (each remote folder to its lake folder) rather than one flow per set. Always
    /// at least one; the single-transfer case is a one-step list. <see cref="Overwrite"/> and
    /// <see cref="PreserveStructure"/> apply to every step.</summary>
    public required IReadOnlyList<SftpStep> Steps { get; init; }

    /// <summary>Overwrite an existing destination file; when false a collision fails rather than clobbers.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Preserve the source's folder structure under the destination root; flat by file name otherwise.</summary>
    public bool PreserveStructure { get; init; } = true;

    /// <summary>On a download, skip writing a lake/local file whose target already holds byte-identical content, so an
    /// unchanged re-download does not bump its last-modified time and re-trigger downstream ingestion. On by default.
    /// Turn it off to write every downloaded file unconditionally, avoiding the per-file hash comparison. Has no effect
    /// on an upload, or when <see cref="Overwrite"/> is false.</summary>
    public bool SkipUnchanged { get; init; } = true;

    /// <summary>Optional explicit declaration of the file set(s) this transfer produces, for lineage. By default
    /// lineage is computed from the steps themselves (each download step's local target is a written file node the
    /// downstream ingestion reads). Declare outputs only to override that, for a step whose consumable folder differs
    /// from its physical target: each entry binds independently, by folder, file-name glob, or path regex, to every
    /// ingestion that reads it, so every consumer of a downloaded file gets an edge from this flow.</summary>
    public IReadOnlyList<FileOutput> Outputs { get; init; } = [];
}

/// <summary>One transfer within an SFTP flow: a remote path paired with a local path and its own file selection. A
/// flow lists as many steps as it needs, so one pipeline downloads (or uploads) many file sets over one connection.</summary>
public sealed record SftpStep
{
    /// <summary>The non-SFTP side: a data-lake URI (<c>abfss://…</c> / https) or a local/UNC path. On a download it is
    /// the target files are written to; on an upload it is the source files are read from.</summary>
    public required string Local { get; init; }

    /// <summary>The directory on the SFTP server. On download it is listed; on upload it is written to.</summary>
    public string RemotePath { get; init; } = ".";

    /// <summary>File-name glob selecting which files transfer. Default <c>*</c>.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Recurse into subdirectories under the root. Default true.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Only transfer files modified within this many days (0 = all).</summary>
    public int ModifiedWithinDays { get; init; }
}

/// <summary>The direction an SFTP flow moves files.</summary>
public enum SftpDirection
{
    /// <summary>Server -> lake/local.</summary>
    Download,

    /// <summary>Lake/local -> server.</summary>
    Upload,
}

/// <summary>The SFTP server and its credentials. Secret material is always a <c>${...}</c> reference, never inline.</summary>
public sealed record SftpServer
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    /// <summary>The SFTP username (a coordinate, not a secret).</summary>
    public required string Username { get; init; }

    /// <summary>Secret reference to the password; an alternative to <see cref="PrivateKeyRef"/>.</summary>
    public string? PasswordRef { get; init; }

    /// <summary>Secret reference to the private-key PEM.</summary>
    public string? PrivateKeyRef { get; init; }

    /// <summary>Secret reference to the passphrase protecting <see cref="PrivateKeyRef"/>.</summary>
    public string? PassphraseRef { get; init; }
}

/// <summary>The outcome of an SFTP run: what moved, how much, any error. Never throws for a transfer failure.</summary>
public sealed record SftpRunResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double DurationSeconds { get; init; }
    public int Matched { get; init; }
    public int FilesTransferred { get; init; }

    /// <summary>Files (downloads) left untouched because the target already held byte-identical content, so its
    /// last-modified time was not bumped and downstream ingestion is not re-triggered for them.</summary>
    public int FilesSkipped { get; init; }

    public long BytesTransferred { get; init; }
    public IReadOnlyList<SftpFileResult> Files { get; init; } = [];
}

/// <summary>One file transferred by an SFTP run.</summary>
/// <param name="Location">The resolved absolute location written (or the remote target on an upload).</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="Hash">The content hash (lowercase hex MD5) of a downloaded file's bytes; null for an upload.</param>
public sealed record SftpFileResult(string Location, long SizeBytes, string? Hash = null);
