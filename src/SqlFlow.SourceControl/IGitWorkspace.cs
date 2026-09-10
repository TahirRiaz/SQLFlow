namespace SqlFlow.SourceControl;

/// <summary>Resolved git credentials for an HTTPS remote: a username plus a secret (a BitBucket app password or
/// a GitHub personal access token). Already expanded from their <c>${...}</c> references, so this type is only
/// ever held in memory for the duration of a push.</summary>
public sealed record GitCredentials
{
    /// <summary>The git username. BitBucket needs the account username; GitHub accepts any non-empty value
    /// alongside a token, so null defaults to a conventional placeholder at push time.</summary>
    public string? Username { get; init; }

    public required string Secret { get; init; }
}

/// <summary>The static configuration of a source-control git working tree: where it lives, its remote and
/// branch, the commit identity, the credentials, and the single database folder this flow owns (so commits are
/// scoped to that subtree and several flows can share one repository).</summary>
public sealed record GitWorkspaceConfig
{
    public required string WorkingDirectory { get; init; }
    public string? Remote { get; init; }
    public string Branch { get; init; } = "main";
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public GitCredentials? Credentials { get; init; }

    /// <summary>Whether a successful commit is pushed to <see cref="Remote"/>. False keeps the commit local even
    /// when a remote is set (the <c>--no-push</c> case), while the remote is still used to clone the baseline.</summary>
    public bool Push { get; init; } = true;

    /// <summary>The repository-relative folder (the database name) this flow's commits are scoped to.</summary>
    public required string PathScope { get; init; }
}

/// <summary>The outcome of committing (and optionally pushing) a snapshot.</summary>
public sealed record GitCommitResult
{
    /// <summary>True when there was a real change and a commit was created; false when the working tree already
    /// matched the last commit (nothing to record).</summary>
    public bool Committed { get; init; }

    public string? CommitSha { get; init; }

    /// <summary>True when the commit was pushed to the remote; false when no remote is configured or there was
    /// nothing to push.</summary>
    public bool Pushed { get; init; }

    /// <summary>The number of staged path changes the commit captured.</summary>
    public int FilesChanged { get; init; }
}

/// <summary>
/// A git working tree the snapshot is committed into. <see cref="EnsureReady"/> makes the tree exist and sit on
/// the target branch (cloning from the remote, or initializing a local repository) BEFORE the snapshot files are
/// written; <see cref="Commit"/> then stages the database subtree, commits if anything changed, and pushes when
/// a remote is configured. Pushes are fast-forward only; the snapshotter never force-pushes or rewrites history.
/// </summary>
public interface IGitWorkspace
{
    void EnsureReady(CancellationToken ct = default);

    GitCommitResult Commit(string message, CancellationToken ct = default);
}
