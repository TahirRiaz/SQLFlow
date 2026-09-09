namespace SqlFlow.Core.SourceControl;

/// <summary>
/// A source-control flow (flowType: scm): the V3 equivalent of one <c>flw.SysSourceControl</c> row. It scripts
/// the full object definition of one SQLFlow-managed SQL Server database to disk and commits the snapshot to a
/// git repository, so the schema's change history lives in version control. Re-running it over time is what
/// produces the diff history. The database is identified by a declared connection (resolved through the same
/// secretless pipeline as every other flow); the repository, its remote, and the git credentials are all
/// references, never literals.
/// </summary>
public sealed record SourceControlFlow
{
    /// <summary>The stable, name-derived flow id (logs and artifacts key on it without a control database).</summary>
    public required int FlowId { get; init; }

    /// <summary>The flow name (legacy SysAlias); the snapshot's identity and run-history folder.</summary>
    public required string SysAlias { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public string? Description { get; init; }

    /// <summary>The grouping label (the legacy <c>flw.SysSourceControl.Batch</c>), a filter for listing and
    /// triggering related snapshots together. Purely a label: it carries no ordering and no schedule, exactly as
    /// it does on every other flow kind.</summary>
    public string? Batch { get; init; }

    /// <summary>The name of the connection (declared under <c>connections:</c>) for the database to script.</summary>
    public required string Server { get; init; }

    /// <summary>The connection reference the resolver expects: the declared connection addressed as an
    /// <c>@alias</c> (the same convention every other flow uses).</summary>
    public string ConnectionReference => "@" + Server;

    /// <summary>An explicit database name to script; null means the connection's default catalog (DB_NAME()).
    /// Scripted objects land under a folder named for the resolved database, matching the legacy layout.</summary>
    public string? Database { get; init; }

    public required SourceControlRepository Repository { get; init; }

    public SourceControlScripting Scripting { get; init; } = new();
}

/// <summary>
/// Where and how the snapshot is committed. <see cref="WorkingDirectory"/> is the local git working tree; with
/// a <see cref="Remote"/> set the snapshot is cloned/pushed there (BitBucket or GitHub over HTTPS), authenticated
/// by <see cref="Username"/> plus <see cref="Secret"/> (a BitBucket app password or a GitHub personal access
/// token). Both auth fields are <c>${...}</c> references resolved at push time, so no credential rests in YAML.
/// </summary>
public sealed record SourceControlRepository
{
    /// <summary>The local git working tree the objects are written into (absolute, or resolved against the
    /// document's directory by the CLI). Cloned from <see cref="Remote"/> on first use when a remote is set.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The git remote URL (HTTPS) to push to; null keeps the history purely local (still a real git
    /// repository, just never pushed).</summary>
    public string? Remote { get; init; }

    /// <summary>The branch the snapshot commits land on. Created if the repository is empty.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>A <c>${...}</c> reference to the git username (BitBucket username, or any value with a GitHub
    /// PAT). Required to push to <see cref="Remote"/>; ignored for local-only history.</summary>
    public string? Username { get; init; }

    /// <summary>A <c>${...}</c> reference to the git secret: a BitBucket app password or a GitHub personal
    /// access token. Required to push; never a literal.</summary>
    public string? Secret { get; init; }

    /// <summary>The commit author/committer display name.</summary>
    public string AuthorName { get; init; } = "SQLFlow";

    /// <summary>The commit author/committer email.</summary>
    public string AuthorEmail { get; init; } = "sqlflow@localhost";
}

/// <summary>
/// What the scripter captures. By default every supported object category (<see cref="SourceControlObjectTypes"/>)
/// is scripted schema-only, except the engine's own staging schema (<see cref="ExcludeSchemas"/>);
/// <see cref="IncludeTypes"/>/<see cref="ExcludeTypes"/> narrow the categories, and
/// <see cref="DataTables"/> additionally scripts the row data of the named tables (the legacy
/// <c>ScriptDataForTables</c>), as INSERT statements, for reference/seed tables worth versioning.
/// </summary>
public sealed record SourceControlScripting
{
    /// <summary>How many connections script objects at once when nothing says otherwise. SMO spends dozens of
    /// small round trips on a single table, so a snapshot's cost is latency rather than server load and the walk
    /// scales almost linearly with the lane count; eight is comfortably below any sane connection budget while
    /// turning a several-minute walk over a few hundred objects into well under a minute.</summary>
    public const int DefaultParallelism = 8;

    /// <summary>The largest lane count a flow may ask for. Past this the added lanes stop buying wall-clock (the
    /// enumeration and the writing dominate) and only add connections to a production server.</summary>
    public const int MaximumParallelism = 32;

    /// <summary>Tables whose row data is scripted (in addition to their schema), each a <c>schema.table</c>
    /// name (case-insensitive). Empty means schema-only for every table.</summary>
    public IReadOnlyList<string> DataTables { get; init; } = [];

    /// <summary>If non-empty, only these object categories are scripted; otherwise all supported categories are.
    /// Each entry is one of <see cref="SourceControlObjectTypes.All"/>.</summary>
    public IReadOnlyList<string> IncludeTypes { get; init; } = [];

    /// <summary>Object categories to skip, applied after <see cref="IncludeTypes"/>.</summary>
    public IReadOnlyList<string> ExcludeTypes { get; init; } = [];

    /// <summary>
    /// Schemas whose objects are not scripted at all (the schema itself, and every table, view, procedure, or
    /// other schema-qualified object in it), compared case-insensitively. Defaults to
    /// <see cref="SourceControlObjectTypes.DefaultExcludedSchemas"/>, the engine's staging schema: its tables
    /// are per-flow work tables that each run rebuilds and drops, so versioning them would fill every snapshot
    /// with churn over objects that are not part of the database's definition, and a table dropped mid-walk
    /// would fail the run outright. Set the list explicitly (including to empty) to script them anyway.
    /// </summary>
    public IReadOnlyList<string> ExcludeSchemas { get; init; } = SourceControlObjectTypes.DefaultExcludedSchemas;

    /// <summary>How many connections script objects concurrently, between 1 and
    /// <see cref="MaximumParallelism"/>. Each lane is an independent connection with its own SMO server, so the
    /// snapshot it produces is identical whatever this is set to; only the wall-clock and the load on the
    /// scripted server change. Set it to 1 to walk the database on a single connection.</summary>
    public int Parallelism { get; init; } = DefaultParallelism;
}
