namespace SqlFlow.SourceControl;

/// <summary>How a source-control run behaves: a dry run scripts and writes the working tree but never commits;
/// <see cref="Push"/> controls whether a commit is pushed to the remote.</summary>
public sealed record SourceControlRunOptions
{
    public bool DryRun { get; init; }

    public bool Push { get; init; } = true;

    /// <summary>An orchestrator-assigned run id the service stamps on the run instead of minting its own, so a
    /// control-plane trigger records the snapshot under the id it already handed the caller. Null mints a fresh
    /// id, preserving the existing direct-run behavior.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Where the run narrates itself: each stage publishes a <see cref="SqlFlow.Core.Model.FlowEvent"/>
    /// here, which is the stream the live trace panel reads and <c>run.json</c> persists. Null runs silently,
    /// which is what a direct call with no orchestrator wants.</summary>
    public SqlFlow.Core.Abstractions.IFlowEventSink? Events { get; init; }
}

/// <summary>
/// The outcome of one source-control snapshot: the scripted object count, what the working-tree write changed,
/// whether a commit was created and pushed, and any warnings (encrypted modules, categories that could not be
/// scripted). This is the canonical run product, serialized to <c>scm.json</c> in the run history.
/// </summary>
public sealed record SourceControlResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }

    public string? DatabaseName { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Remote { get; init; }
    public required string Branch { get; init; }
    public bool DryRun { get; init; }

    public int ObjectsScripted { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Deleted { get; init; }
    public int Unchanged { get; init; }

    public bool Committed { get; init; }
    public string? CommitSha { get; init; }
    public bool Pushed { get; init; }

    public IReadOnlyList<string> Objects { get; init; } = [];

    /// <summary>The repository-relative paths this run created, i.e. objects that did not exist in the previous
    /// snapshot. Kept alongside the counts so the catalog can answer "what changed in this database, and when"
    /// without anyone reading the git history.</summary>
    public IReadOnlyList<string> AddedObjects { get; init; } = [];

    /// <summary>The paths whose scripted definition differs from the previous snapshot: the actual schema edits.</summary>
    public IReadOnlyList<string> ChangedObjects { get; init; } = [];

    /// <summary>The paths removed because their object no longer exists in the database (a drop or a rename).</summary>
    public IReadOnlyList<string> DeletedObjects { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public double DurationSeconds { get; init; }
}
