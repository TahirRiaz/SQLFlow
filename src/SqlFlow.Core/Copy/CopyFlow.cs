using SqlFlow.Core.Files;
using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Copy;

/// <summary>
/// A validated file-copy flow (<c>flowType: cpy</c>): moves files between endpoints, byte-for-byte, in any
/// direction - storage account to storage account, local disk to storage, storage to local disk, local to local.
/// It performs no parsing or reshaping; it optionally zips the matched files into one archive or unzips archives on
/// the way through. It consolidates the estate's lake-to-lake / drop-zone copy runbooks (e.g. baatbooking) into one
/// declarative, testable, schedulable engine, the copy counterpart to the acquisition (<c>acq</c>) engine that
/// fetches from third parties. SFTP is a separate flow type (<c>sftp</c>), not a copy endpoint.
/// </summary>
public sealed record CopyFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    /// <summary>The copy steps this flow performs, in order: each a source-to-target file transfer. One flow copies as
    /// many file sets as it declares, so a whole source system is one pipeline (e.g. a vendor's DETAIL, SESS, TRANS
    /// folders each to their lake folder) rather than one flow per folder. Always at least one; the single-copy case
    /// is a one-step list. The flow-level <see cref="Operation"/> and <see cref="Options"/> apply to every step.</summary>
    public required IReadOnlyList<CopyStep> Steps { get; init; }

    /// <summary>What the flow does with each step's matched files: copy them verbatim, zip them into one archive, or
    /// unzip each matched archive. Default <see cref="CopyOperation.Copy"/>.</summary>
    public CopyOperation Operation { get; init; } = CopyOperation.Copy;

    public CopyOptions Options { get; init; } = new();

    /// <summary>Optional explicit declaration of the file set(s) this copy produces, for lineage. By default lineage
    /// is computed from the steps themselves (each step's target is a written file node the downstream ingestion
    /// reads). Declare outputs only to override that, for a step whose consumable folder differs from its physical
    /// target (e.g. <c>preserveStructure</c> lands per-object subfolders): each entry binds independently, by folder,
    /// file-name glob, or path regex, to every ingestion that reads it.</summary>
    public IReadOnlyList<FileOutput> Outputs { get; init; } = [];
}

/// <summary>One source-to-target file transfer within a copy flow. A flow lists as many steps as it needs, so one
/// pipeline copies many file sets, each with its own source selection and target.</summary>
public sealed record CopyStep
{
    public required CopyEndpoint Source { get; init; }

    public required CopyEndpoint Target { get; init; }
}

/// <summary>The transformation a copy flow applies as it moves files.</summary>
public enum CopyOperation
{
    /// <summary>Copy each matched file to the target verbatim.</summary>
    Copy,

    /// <summary>Bundle every matched source file into a single <c>.zip</c> archive written to the target.</summary>
    Zip,

    /// <summary>Extract each matched source archive (a <c>.zip</c>) and write its entries to the target.</summary>
    Unzip,
}

/// <summary>
/// One side of a copy: where the files are, how to select them (source side), and how to authenticate. The
/// <see cref="Location"/> scheme selects the endpoint implementation: a local/UNC path or an Azure Blob / ADLS Gen2
/// URI (<c>abfss://fs@account.dfs.core.windows.net/path</c> or the https form). Selection fields
/// (<see cref="Pattern"/>, <see cref="Recursive"/>, <see cref="ModifiedWithinDays"/>) apply only to the source; the
/// target ignores them.
/// </summary>
public sealed record CopyEndpoint
{
    /// <summary>The root location: a local/UNC path or an Azure storage URI.</summary>
    public required string Location { get; init; }

    /// <summary>File-name glob selecting which files under the root are copied (source side). Default <c>*</c>.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Recurse into subfolders under the root (source side). Default true.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Only copy files modified within this many days (source side); 0 (default) copies everything.</summary>
    public int ModifiedWithinDays { get; init; }

    /// <summary>Optional secret reference to a connection string authenticating an Azure endpoint. When absent the
    /// ambient managed identity / az login (<c>SQLFLOW_AZURE_AUTH</c>) authenticates, so no per-flow secret is needed.</summary>
    public string? ConnectionStringRef { get; init; }

    /// <summary>Optional secret reference to a SAS token authenticating an Azure endpoint.</summary>
    public string? SasTokenRef { get; init; }

    /// <summary>Optional secret reference to a storage account key authenticating an Azure endpoint (the shape the
    /// legacy runbooks used). Prefer managed identity where the drop zone allows it.</summary>
    public string? AccountKeyRef { get; init; }

    /// <summary>Secret reference to the AWS access key id authenticating an <c>s3://</c> endpoint. Required for S3
    /// (it has no ambient identity), paired with <see cref="SecretKeyRef"/>; ignored by the other schemes.</summary>
    public string? AccessKeyRef { get; init; }

    /// <summary>Secret reference to the AWS secret access key authenticating an <c>s3://</c> endpoint.</summary>
    public string? SecretKeyRef { get; init; }

    /// <summary>The AWS region of an <c>s3://</c> endpoint (e.g. <c>eu-west-1</c>). Defaults to <c>eu-west-1</c> when
    /// unset; ignored by the other schemes.</summary>
    public string? Region { get; init; }
}

/// <summary>Copy behavior shared across operations.</summary>
public sealed record CopyOptions
{
    /// <summary>Overwrite an existing target file; when false a copy that would collide fails rather than clobber.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Preserve each source file's folder structure (relative to the source root) under the target. When
    /// false every file lands flat under the target root by its name.</summary>
    public bool PreserveStructure { get; init; } = true;

    /// <summary>Skip writing a file whose target copy already holds byte-identical content, so an unchanged re-run does
    /// not bump the target's last-modified time and re-trigger downstream ingestion. On by default. Turn it off to
    /// force every matched file to be rewritten unconditionally, avoiding the one target listing (and, for a local
    /// target, the per-file hashing) the comparison costs. Has no effect when <see cref="Overwrite"/> is false.</summary>
    public bool SkipUnchanged { get; init; } = true;

    /// <summary>The archive name for <see cref="CopyOperation.Zip"/>; when absent it is <c>&lt;flow&gt;_&lt;timestamp&gt;.zip</c>.</summary>
    public string? ZipName { get; init; }
}

/// <summary>The outcome of a copy run: what moved, how much, and any error. Never throws for a transfer failure -
/// the runner returns a failed/partial result, so a batch member behaves like a directly-invoked flow.</summary>
public sealed record CopyRunResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>Files matched at the source (before the operation).</summary>
    public int Matched { get; init; }

    /// <summary>Files (or archive entries) written to the target.</summary>
    public int FilesWritten { get; init; }

    /// <summary>Files (or archive entries) left untouched because the target already held byte-identical content, so
    /// the target's last-modified time was not bumped and downstream ingestion is not re-triggered for them.</summary>
    public int FilesSkipped { get; init; }

    public long BytesWritten { get; init; }

    /// <summary>Each written target file, for the run manifest / catalog RunFile rows.</summary>
    public IReadOnlyList<CopyFileResult> Files { get; init; } = [];
}

/// <summary>One file written by a copy run.</summary>
/// <param name="Location">The resolved absolute location written.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="Hash">The content hash (lowercase hex MD5) of the written bytes, recorded on the run's file manifest
/// so the catalog can surface it and a later comparison can tell whether the file changed.</param>
public sealed record CopyFileResult(string Location, long SizeBytes, string? Hash = null);
