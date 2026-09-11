using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The EF Core context for the shadow catalog (schema <c>catalog</c>). EF owns this schema: it is created and
/// upgraded by migrations (<see cref="CatalogDatabase.MigrateAsync"/>), never by hand. This is the only place in
/// the product that uses Entity Framework; the engine stays on direct ADO.NET.
/// </summary>
public sealed class CatalogDbContext : DbContext
{
    public const string SchemaName = "catalog";

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<CatalogRepo> Repos => Set<CatalogRepo>();

    public DbSet<CatalogPipeline> Pipelines => Set<CatalogPipeline>();

    public DbSet<CatalogRun> Runs => Set<CatalogRun>();

    public DbSet<CatalogRunGroup> RunGroups => Set<CatalogRunGroup>();

    public DbSet<CatalogFlowVersion> FlowVersions => Set<CatalogFlowVersion>();

    public DbSet<CatalogObject> Objects => Set<CatalogObject>();

    public DbSet<CatalogLineageEdge> LineageEdges => Set<CatalogLineageEdge>();

    public DbSet<CatalogObjectRelationship> ObjectRelationships => Set<CatalogObjectRelationship>();

    public DbSet<CatalogSubscriber> Subscribers => Set<CatalogSubscriber>();

    public DbSet<CatalogSubscriberQuery> SubscriberQueries => Set<CatalogSubscriberQuery>();

    public DbSet<CatalogFlowDependency> FlowDependencies => Set<CatalogFlowDependency>();

    public DbSet<CatalogRunFile> RunFiles => Set<CatalogRunFile>();

    public DbSet<CatalogRunAssertion> RunAssertions => Set<CatalogRunAssertion>();

    public DbSet<CatalogRunStatement> RunStatements => Set<CatalogRunStatement>();

    public DbSet<CatalogRunEvent> RunEvents => Set<CatalogRunEvent>();

    public DbSet<CatalogMaintenanceSetting> MaintenanceSettings => Set<CatalogMaintenanceSetting>();

    public DbSet<CatalogRunSurrogateKey> RunSurrogateKeys => Set<CatalogRunSurrogateKey>();

    public DbSet<CatalogRunHealthCheckMetric> RunHealthCheckMetrics => Set<CatalogRunHealthCheckMetric>();

    public DbSet<CatalogSchemaChange> SchemaChanges => Set<CatalogSchemaChange>();

    public DbSet<CatalogObjectColumn> ObjectColumns => Set<CatalogObjectColumn>();

    public DbSet<CatalogPipelineColumn> PipelineColumns => Set<CatalogPipelineColumn>();

    public DbSet<CatalogSchedule> Schedules => Set<CatalogSchedule>();

    public DbSet<CatalogScheduleMember> ScheduleMembers => Set<CatalogScheduleMember>();

    public DbSet<CatalogScheduleParent> ScheduleParents => Set<CatalogScheduleParent>();

    public DbSet<CatalogNode> Nodes => Set<CatalogNode>();

    public DbSet<CatalogWorkerPoolDesired> WorkerPools => Set<CatalogWorkerPoolDesired>();

    /// <summary>The dispatch ownership lease rows (one per lease name).</summary>
    public DbSet<CatalogDispatchLease> DispatchLeases => Set<CatalogDispatchLease>();

    public DbSet<CatalogRepoSource> RepoSources => Set<CatalogRepoSource>();

    public DbSet<CatalogActivityEvent> ActivityEvents => Set<CatalogActivityEvent>();

    public DbSet<CatalogUser> Users => Set<CatalogUser>();

    public DbSet<CatalogRole> Roles => Set<CatalogRole>();

    public DbSet<CatalogAccessToken> AccessTokens => Set<CatalogAccessToken>();

    public DbSet<CatalogComputeTask> ComputeTasks => Set<CatalogComputeTask>();

    /// <summary>Prepared ad-hoc queries awaiting approval; the confirmation gate for the query surface.</summary>
    public DbSet<CatalogQueryPlan> QueryPlans => Set<CatalogQueryPlan>();

    public DbSet<CatalogNotificationEvent> NotificationEvents => Set<CatalogNotificationEvent>();

    public DbSet<CatalogChatConversation> ChatConversations => Set<CatalogChatConversation>();

    public DbSet<CatalogChatMessage> ChatMessages => Set<CatalogChatMessage>();

    public DbSet<CatalogNotificationSubscription> NotificationSubscriptions => Set<CatalogNotificationSubscription>();

    public DbSet<CatalogNotificationDelivery> NotificationDeliveries => Set<CatalogNotificationDelivery>();

    public DbSet<CatalogNotificationWatermark> NotificationWatermarks => Set<CatalogNotificationWatermark>();

    /// <summary>The estate digests: periodic and on-demand summaries of the notification event stream.</summary>
    public DbSet<CatalogNotificationDigest> NotificationDigests => Set<CatalogNotificationDigest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<CatalogRepo>(entity =>
        {
            entity.ToTable("Repo");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Name).HasMaxLength(256).IsRequired();
            entity.Property(r => r.RemoteUrl).HasMaxLength(1024);
            entity.Property(r => r.RootPath).HasMaxLength(1024);
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<CatalogPipeline>(entity =>
        {
            entity.ToTable("Pipeline");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(400).IsRequired();
            entity.Property(p => p.Kind).HasMaxLength(16).IsRequired();
            entity.Property(p => p.Batch).HasMaxLength(250);
            entity.Property(p => p.ExecutionMode).HasMaxLength(16).IsRequired();
            entity.Property(p => p.Lifecycle).HasMaxLength(16).IsRequired();
            entity.Property(p => p.RelativePath).HasMaxLength(1024).IsRequired();
            entity.Property(p => p.SourceServer).HasMaxLength(512);
            entity.Property(p => p.TargetServer).HasMaxLength(512);
            entity.Property(p => p.ContentHash).HasMaxLength(64);
            // A flow name is unique WITHIN a repo, not globally: the same name can exist in different repos and
            // they are distinct pipelines (the catalog spans repos).
            entity.HasIndex(p => new { p.RepoId, p.Name }).IsUnique();
            entity.HasIndex(p => p.Name);
            entity.HasIndex(p => p.Kind);
            entity.HasIndex(p => p.Active);
            entity.HasIndex(p => new { p.RepoId, p.Wave }); // list a repo's pipelines in execution order
        });

        modelBuilder.Entity<CatalogRun>(entity =>
        {
            entity.ToTable("Run");
            entity.HasKey(r => r.RunId);
            entity.Property(r => r.FlowName).HasMaxLength(400).IsRequired();
            entity.Property(r => r.FlowKind).HasMaxLength(16).IsRequired();
            entity.Property(r => r.Status).HasMaxLength(16).IsRequired();
            entity.Property(r => r.ClaimedByNode).HasMaxLength(256);
            entity.Property(r => r.TargetPool).HasMaxLength(128);
            entity.Property(r => r.CommitSha).HasMaxLength(64);
            entity.Property(r => r.FlowVersionHash).HasMaxLength(64);
            entity.Property(r => r.FilePattern).HasMaxLength(200);
            entity.Property(r => r.SourceFilter).HasMaxLength(4000);
            entity.Property(r => r.Host).HasMaxLength(256);
            entity.Property(r => r.IncrementalMode).HasMaxLength(16);
            entity.Property(r => r.IncrementalFilter).HasMaxLength(2048);
            entity.Property(r => r.IncrementalWatermark).HasMaxLength(512);
            entity.Property(r => r.IncrementalWatermarkSource).HasMaxLength(256);
            entity.Property(r => r.DataSetConvention).HasMaxLength(128);
            entity.Property(r => r.TriggerSource).HasMaxLength(16);
            // PipelineId is a soft link (no FK): a run can outlive its pipeline being removed from git, so the
            // history stays even when the Pipeline row is gone. The GUI left-joins on it; it is indexed for that.
            entity.HasIndex(r => r.PipelineId);
            entity.HasIndex(r => r.RepoId);
            entity.HasIndex(r => r.FlowName);
            entity.HasIndex(r => r.WrittenUtc);
            // The work-queue claim seeks the oldest run in a given state: WHERE Status = 'queued' ORDER BY
            // EnqueuedUtc. This composite index makes that a range seek instead of a scan, and also serves the
            // "recover runs stuck running" recovery query.
            entity.HasIndex(r => new { r.Status, r.EnqueuedUtc });
            // The latest-run board resolves each pipeline's newest run (top-1 per pipeline by WrittenUtc, then
            // RunId): this composite serves that as a per-pipeline seek in exactly the query's order.
            entity.HasIndex(r => new { r.PipelineId, r.WrittenUtc, r.RunId }).IsDescending(false, true, true);
            // The run-trace prune identifies its candidates as terminal-but-not-failed runs older than the retention
            // window: WHERE Status IN ('succeeded','cancelled','skipped') AND WrittenUtc < @cutoff. This composite
            // makes that a seek and carries PipelineId so the "is there a newer run for this pipeline" check that
            // decides the candidate is not the latest is covered without a lookup back to the base row.
            entity.HasIndex(r => new { r.Status, r.WrittenUtc }).IncludeProperties(r => r.PipelineId);
            // The group-gating claim subquery asks "does this group have an unfinished member in a lower wave":
            // WHERE GroupId = @g AND GroupWave < @w AND Status IN ('queued','running'). This composite makes that
            // a seek, and also serves the group-detail board (a group's members by wave) and the groupId filter.
            entity.HasIndex(r => new { r.GroupId, r.GroupWave, r.Status });
            // At most ONE running execution per pipeline, as defense in depth behind the dispatcher. The control
            // plane's in-memory dispatcher is the single authority that hands runs out, and its pipeline gate is
            // exact under one lock; this filtered unique index is what turns a bug in that authority (or two
            // dispatchers briefly overlapping during an ownership hand-over) into a loud duplicate-key failure on
            // the hand-out's journal write instead of two overlapping executions. That matters because the engine
            // names a flow's work tables per FLOW, not per run, so two overlapping executions would share (and
            // drop) one staging table.
            entity.HasIndex(r => r.PipelineId, "UX_Run_RunningPipeline")
                .IsUnique()
                .HasFilter($"[Status] = '{RunStatuses.Running}'");
        });

        modelBuilder.Entity<CatalogRunGroup>(entity =>
        {
            entity.ToTable("RunGroup");
            entity.HasKey(g => g.GroupId);
            entity.Property(g => g.Mode).HasMaxLength(16).IsRequired();
            entity.Property(g => g.Anchor).HasMaxLength(400).IsRequired();
            entity.Property(g => g.CommitSha).HasMaxLength(64);
            // A repo's run groups, newest first, for the group board.
            entity.HasIndex(g => new { g.RepoId, g.EnqueuedUtc });
        });

        modelBuilder.Entity<CatalogFlowVersion>(entity =>
        {
            entity.ToTable("FlowVersion");
            // Content-addressed: the hash IS the identity (same hash = same bytes), assigned by code, never by
            // the database. Yaml stays nvarchar(max): a flow document has no useful length bound.
            entity.HasKey(v => v.ContentHash);
            entity.Property(v => v.ContentHash).HasMaxLength(64).ValueGeneratedNever();
            entity.Property(v => v.Yaml).IsRequired();
        });

        modelBuilder.Entity<CatalogObject>(entity =>
        {
            entity.ToTable("Object");
            entity.HasKey(o => o.Key);
            entity.Property(o => o.Key).HasMaxLength(900);
            entity.Property(o => o.ServerRef).HasMaxLength(512).IsRequired();
            entity.Property(o => o.Database).HasMaxLength(256);
            entity.Property(o => o.Schema).HasMaxLength(256);
            entity.Property(o => o.Name).HasMaxLength(512).IsRequired();
            entity.Property(o => o.Kind).HasMaxLength(32);
            // The module body is unbounded; nvarchar(max) so a long proc/view definition is never truncated.
            entity.Property(o => o.Definition);
            // The generating DDL is unbounded too (a wide CREATE TABLE, a long view body); nvarchar(max).
            entity.Property(o => o.Script);
            entity.Property(o => o.ScriptTier).HasMaxLength(16);
            // The interpreted key: a handful of column names, never a blob; 1024 covers a wide composite key.
            entity.Property(o => o.KeyColumns).HasMaxLength(1024);
            entity.Property(o => o.KeyOrigin).HasMaxLength(16);
            // A DB-generated surrogate that serves only as the full-text KEY INDEX (Key is too wide to be one).
            entity.Property(o => o.FullTextKey).UseIdentityColumn();
            entity.HasIndex(o => o.FullTextKey).IsUnique();
            entity.HasIndex(o => o.Name);
            entity.HasIndex(o => o.ServerRef);
        });

        modelBuilder.Entity<CatalogLineageEdge>(entity =>
        {
            entity.ToTable("LineageEdge");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Flow).HasMaxLength(400);
            entity.Property(e => e.ViaModule).HasMaxLength(900);
            entity.Property(e => e.Relation).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(e => e.ObjectName).HasMaxLength(512);
            entity.Property(e => e.Tier).HasMaxLength(16).IsRequired();
            // The hot queries: every edge on an object (cross-repo "what touches X"), and a flow's edges.
            entity.HasIndex(e => e.ObjectKey);
            entity.HasIndex(e => e.RepoId);
            entity.HasIndex(e => e.PipelineId);
        });

        modelBuilder.Entity<CatalogObjectRelationship>(entity =>
        {
            entity.ToTable("ObjectRelationship");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Name).HasMaxLength(512);
            entity.Property(r => r.FromObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(r => r.FromColumns).HasMaxLength(1024).IsRequired();
            entity.Property(r => r.ToObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(r => r.ToColumns).HasMaxLength(1024).IsRequired();
            // Empty for an equi-join (the common case) and for a declared constraint; a range join carries one
            // operator per column pair, so this is bounded by the column list it parallels.
            entity.Property(r => r.Operators).HasMaxLength(128).IsRequired();
            // At most the five join types, comma-joined.
            entity.Property(r => r.JoinTypes).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Origin).HasMaxLength(16).IsRequired();
            entity.Property(r => r.Tier).HasMaxLength(16).IsRequired();
            // The hot queries: an object's relationships in either direction, and the per-repo replacement.
            entity.HasIndex(r => r.FromObjectKey);
            entity.HasIndex(r => r.ToObjectKey);
            entity.HasIndex(r => r.RepoId);
        });

        modelBuilder.Entity<CatalogSubscriber>(entity =>
        {
            entity.ToTable("Subscriber");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(250).IsRequired();
            entity.Property(s => s.Type).HasMaxLength(250).IsRequired();
            entity.Property(s => s.ObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(s => s.File).HasMaxLength(1024).IsRequired();
            entity.Property(s => s.Owner).HasMaxLength(250);
            entity.Property(s => s.Description).HasMaxLength(1024);
            // Notes is deliberately unbounded where Description is capped: a description is one authored line,
            // whereas a note is whatever a reviewer wrote about the report's state, often several sentences and
            // sometimes several lines. A cap here would turn a long remark into a failed sync.
            entity.Property(s => s.Notes);
            entity.Property(s => s.Url).HasMaxLength(1024);
            // The hot queries: the per-repo replacement, a subscriber by name, and the join back from an edge's
            // ViaModule to the consumer it belongs to.
            entity.HasIndex(s => s.RepoId);
            entity.HasIndex(s => s.Name);
            entity.HasIndex(s => s.ObjectKey);
        });

        modelBuilder.Entity<CatalogSubscriberQuery>(entity =>
        {
            entity.ToTable("SubscriberQuery");
            entity.HasKey(q => q.Id);
            entity.Property(q => q.SubscriberKey).HasMaxLength(900).IsRequired();
            entity.Property(q => q.Name).HasMaxLength(250).IsRequired();
            entity.Property(q => q.ServerRef).HasMaxLength(400).IsRequired();
            entity.Property(q => q.Sql).IsRequired();
            entity.HasIndex(q => q.SubscriberKey);
            entity.HasIndex(q => q.RepoId);
        });

        modelBuilder.Entity<CatalogFlowDependency>(entity =>
        {
            entity.ToTable("FlowDependency");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.FromFlow).HasMaxLength(400).IsRequired();
            entity.Property(d => d.ToFlow).HasMaxLength(400).IsRequired();
            entity.HasIndex(d => d.RepoId);
            entity.HasIndex(d => d.FromPipelineId);
            entity.HasIndex(d => d.ToPipelineId);
        });

        modelBuilder.Entity<CatalogRunFile>(entity =>
        {
            entity.ToTable("RunFile");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Name).HasMaxLength(512).IsRequired();
            entity.Property(f => f.Path).HasMaxLength(1024);
            // Hex hash: 32 chars for MD5 today, sized to hold a SHA-256 (64) without a future migration.
            entity.Property(f => f.Hash).HasMaxLength(128);
            entity.HasIndex(f => f.RunId);
        });

        modelBuilder.Entity<CatalogRunAssertion>(entity =>
        {
            entity.ToTable("RunAssertion");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Name).HasMaxLength(256).IsRequired();
            entity.Property(a => a.Result).HasMaxLength(512);
            entity.Property(a => a.AssertedValue).HasMaxLength(512);
            entity.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<CatalogRunStatement>(entity =>
        {
            entity.ToTable("RunStatement");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Step).HasMaxLength(128).IsRequired();
            // Sql is nvarchar(max): a generated statement (a CREATE TABLE, a MERGE) has no useful length bound.
            entity.Property(s => s.Sql).IsRequired();
            // Error is nvarchar(max), null: only the one statement that threw carries it, holding the raw engine
            // error (a SqlException message can be long), so no length bound applies.
            entity.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<CatalogRunEvent>(entity =>
        {
            entity.ToTable("RunEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Level).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Step).HasMaxLength(128);
            // Message is nvarchar(max): an event message (an engine decision, an error detail) has no useful
            // length bound.
            entity.Property(e => e.Message).IsRequired();
            entity.HasIndex(e => e.RunId);
        });

        modelBuilder.Entity<CatalogMaintenanceSetting>(entity =>
        {
            entity.ToTable("MaintenanceSetting");
            entity.HasKey(s => s.Id);
            // A single operator-set row; the id is the fixed singleton key (1), never database-generated.
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.UpdatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<CatalogRunSurrogateKey>(entity =>
        {
            entity.ToTable("RunSurrogateKey");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.SurrogateTable).HasMaxLength(776).IsRequired();
            entity.Property(s => s.SurrogateColumn).HasMaxLength(256).IsRequired();
            entity.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<CatalogRunHealthCheckMetric>(entity =>
        {
            entity.ToTable("RunHealthCheckMetric");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Name).HasMaxLength(256).IsRequired();
            entity.Property(m => m.ModelTrainer).HasMaxLength(128);
            entity.HasIndex(m => m.RunId);
        });

        modelBuilder.Entity<CatalogSchemaChange>(entity =>
        {
            entity.ToTable("SchemaChange");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Database).HasMaxLength(256).IsRequired();
            entity.Property(c => c.Category).HasMaxLength(64).IsRequired();
            entity.Property(c => c.Schema).HasMaxLength(256);
            entity.Property(c => c.Name).HasMaxLength(512).IsRequired();
            entity.Property(c => c.ChangeType).HasMaxLength(16).IsRequired();
            entity.Property(c => c.CommitSha).HasMaxLength(64);
            // The feed query: the estate's changes newest first, optionally narrowed to one database. Descending
            // on the date so the index serves the ordering, not just the filter.
            entity.HasIndex(c => new { c.RepoId, c.OccurredUtc }).IsDescending(false, true);
            entity.HasIndex(c => new { c.RepoId, c.Database, c.OccurredUtc }).IsDescending(false, false, true);
            // The run drill-down, and the delete-by-run the re-record path needs to stay idempotent.
            entity.HasIndex(c => c.RunId);
            // "Everything that ever happened to this object", the history panel behind one table or view.
            entity.HasIndex(c => new { c.RepoId, c.Database, c.Schema, c.Name });
        });

        modelBuilder.Entity<CatalogObjectColumn>(entity =>
        {
            entity.ToTable("ObjectColumn");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.ObjectKey).HasMaxLength(900).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(512).IsRequired();
            entity.Property(c => c.DataType).HasMaxLength(128);
            entity.Property(c => c.Tier).HasMaxLength(16).IsRequired();
            // Column-by-name search across every object.
            entity.HasIndex(c => c.Name);
            // One row per column position per object: a true invariant (sys.columns.column_id is unique per
            // object) and the idempotency backstop for the sync's delete-by-key + re-insert. The composite also
            // serves the "an object's columns, in order" drill-down (ObjectKey is the leftmost prefix).
            entity.HasIndex(c => new { c.ObjectKey, c.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<CatalogPipelineColumn>(entity =>
        {
            entity.ToTable("PipelineColumn");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            entity.Property(c => c.ColumnName).HasMaxLength(512).IsRequired();
            entity.Property(c => c.SourceColumn).HasMaxLength(512);
            // The expression and type are authored/generated SQL: bounded generously, never a blob, but wide
            // enough for a real CAST/CASE expression.
            entity.Property(c => c.Expression).HasMaxLength(4000);
            entity.Property(c => c.DataType).HasMaxLength(128);
            // A pipeline's transforms, in order, for the drill-down; and column-by-name search across the estate.
            entity.HasIndex(c => c.PipelineId);
            entity.HasIndex(c => c.RepoId);
            entity.HasIndex(c => c.ColumnName);
            // One row per column position per (pipeline, kind): the idempotency backstop for the sync/run's
            // delete-by-(pipeline, kind) + re-insert, and the "a pipeline's declared columns, in order" drill-down.
            entity.HasIndex(c => new { c.PipelineId, c.Kind, c.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<CatalogSchedule>(entity =>
        {
            entity.ToTable("Schedule");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(400).IsRequired();
            entity.Property(s => s.Cron).HasMaxLength(256);
            entity.Property(s => s.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Source).HasMaxLength(16).IsRequired();
            entity.Property(s => s.DefinitionPath).HasMaxLength(1024);
            entity.Property(s => s.DefinitionFlow).HasMaxLength(400);
            entity.Property(s => s.LastStaleParents).HasMaxLength(2000);
            entity.HasIndex(s => s.RepoId);
            // A name is what flows join, so it identifies exactly one schedule in a repo. The estate scan already
            // collapses a redefined name to the first definition; the unique index is what keeps two sync paths (or
            // a yaml schedule and an api one) from racing a second row into existence behind it.
            entity.HasIndex(s => new { s.RepoId, s.Name }).IsUnique();
            // The scheduler scans for due, active schedules ordered by when they are next due:
            // WHERE Enabled = 1 AND Paused = 0 AND NextFireUtc <= now.
            entity.HasIndex(s => s.NextFireUtc);
        });

        modelBuilder.Entity<CatalogScheduleParent>(entity =>
        {
            entity.ToTable("ScheduleParent");
            // A schedule names a given parent at most once; the composite key makes a repeated declaration idempotent
            // rather than a duplicate that would make the fan-in wait on the same parent twice and stall nothing but
            // still read wrong in the API.
            entity.HasKey(p => new { p.ScheduleId, p.ParentName });
            entity.Property(p => p.ParentName).HasMaxLength(400).IsRequired();
            entity.HasIndex(p => p.RepoId);
            // The chained scan is the other half of the scheduler tick, and it drives from this table: every parent
            // row joins to its parent schedule by (RepoId, ParentName). Chained schedules are a small minority of the
            // estate, so scanning this table rather than filtering the whole Schedule table keeps the tick cheap.
            entity.HasIndex(p => new { p.RepoId, p.ParentName });
            entity.HasOne(p => p.Schedule)
                  .WithMany(s => s.Parents)
                  .HasForeignKey(p => p.ScheduleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogScheduleMember>(entity =>
        {
            entity.ToTable("ScheduleMember");
            // A flow joins a schedule at most once; the composite key makes a repeated join idempotent rather than
            // a duplicate that would enqueue the flow twice in one fire.
            entity.HasKey(m => new { m.ScheduleId, m.PipelineId });
            entity.Property(m => m.FlowName).HasMaxLength(400).IsRequired();
            entity.HasIndex(m => m.RepoId);
            // "Which schedules is this flow in?": the flow detail page's Schedules tab, and the only way a flow that
            // declares no schedule of its own can still show the one that runs it.
            entity.HasIndex(m => m.PipelineId);
        });

        modelBuilder.Entity<CatalogNode>(entity =>
        {
            entity.ToTable("Node");
            entity.HasKey(n => n.Name);
            entity.Property(n => n.Name).HasMaxLength(256);
            entity.Property(n => n.Version).HasMaxLength(64);
            entity.Property(n => n.Pool).HasMaxLength(128);
            // The fleet view lists nodes most-recently-seen first, and counts online nodes per pool.
            entity.HasIndex(n => n.LastSeenUtc);
            entity.HasIndex(n => n.Pool);
        });

        modelBuilder.Entity<CatalogDispatchLease>(entity =>
        {
            entity.ToTable("DispatchLease");
            entity.HasKey(l => l.Name);
            entity.Property(l => l.Name).HasMaxLength(64);
            entity.Property(l => l.Owner).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<CatalogWorkerPoolDesired>(entity =>
        {
            entity.ToTable("WorkerPool");
            // One desired-state row per pool; the empty string keys the default (untargeted) pool.
            entity.HasKey(p => p.Pool);
            entity.Property(p => p.Pool).HasMaxLength(128);
            entity.Property(p => p.UpdatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<CatalogRepoSource>(entity =>
        {
            entity.ToTable("RepoSource");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(256).IsRequired();
            entity.Property(s => s.RemoteUrl).HasMaxLength(1024).IsRequired();
            entity.Property(s => s.Branch).HasMaxLength(256).IsRequired();
            // A secret reference (${keyvault:...}/${env:...}), not a secret value: bounded, never a blob.
            entity.Property(s => s.CredentialReference).HasMaxLength(512);
            entity.Property(s => s.CredentialUsername).HasMaxLength(256);
            // A JSON array of excluded flow paths: unbounded (a large estate can exclude many files).
            entity.Property(s => s.ExcludedFlowPaths);
            entity.HasIndex(s => s.Name).IsUnique();
            // The sync loop scans for enabled sources whose next sync is due.
            entity.HasIndex(s => s.NextSyncUtc);
        });

        modelBuilder.Entity<CatalogActivityEvent>(entity =>
        {
            entity.ToTable("ActivityEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SubjectKey).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Level).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Step).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(16);
            // Message is nvarchar(max): an activity line (a clone error, a lineage warning) has no useful bound.
            entity.Property(e => e.Message).IsRequired();
            // The trace stream tails one (kind, subject)'s events in id order; also the prune-old-activities scan.
            entity.HasIndex(e => new { e.Kind, e.SubjectKey, e.Id });
        });

        modelBuilder.Entity<CatalogRole>(entity =>
        {
            entity.ToTable("Role");
            entity.HasKey(r => r.Name);
            entity.Property(r => r.Name).HasMaxLength(64);
            entity.Property(r => r.Scopes).HasMaxLength(256).IsRequired();
            entity.Property(r => r.Description).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<CatalogUser>(entity =>
        {
            entity.ToTable("User");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).HasMaxLength(256).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(320);
            entity.Property(u => u.DisplayName).HasMaxLength(256);
            // PBKDF2 hashes render well under 512 chars; bounded so the column is never an accidental blob.
            entity.Property(u => u.PasswordHash).HasMaxLength(512);
            entity.Property(u => u.Role).HasMaxLength(64).IsRequired();
            entity.Property(u => u.Provider).HasMaxLength(16).IsRequired();
            entity.Property(u => u.ExternalObjectId).HasMaxLength(64);
            // One account per sign-in name across providers, so a token subject is always unambiguous.
            entity.HasIndex(u => u.Username).IsUnique();
            // JIT provisioning keys on the Entra object id; filtered unique so local users (null) do not collide.
            entity.HasIndex(u => u.ExternalObjectId).IsUnique().HasFilter("[ExternalObjectId] IS NOT NULL");
        });

        modelBuilder.Entity<CatalogComputeTask>(entity =>
        {
            entity.ToTable("ComputeTask");
            entity.HasKey(t => t.TaskId);
            entity.Property(t => t.Operation).HasMaxLength(32).IsRequired();
            entity.Property(t => t.SourceRef).HasMaxLength(512).IsRequired();
            entity.Property(t => t.ProviderKind).HasMaxLength(16);
            // The payload is compact JSON of a bounded contract; nvarchar(max) only because a long ${keyvault:...}
            // reference plus arguments has no single useful column bound.
            entity.Property(t => t.ArgumentsJson).IsRequired();
            entity.Property(t => t.TargetPool).HasMaxLength(128);
            entity.Property(t => t.Status).HasMaxLength(16).IsRequired();
            entity.Property(t => t.RequestedBy).HasMaxLength(256);
            entity.Property(t => t.ClaimedByNode).HasMaxLength(256);
            // Error and ResultJson are nvarchar(max): a provider error can be long, and the result is the
            // operation's JSON document (bounded by the executor's result-size cap, not by a column length).
            // The work-queue claim seeks the oldest queued task, exactly like the run claim.
            entity.HasIndex(t => new { t.Status, t.EnqueuedUtc });
            // The GUI lists recent tasks newest first, optionally per source.
            entity.HasIndex(t => t.EnqueuedUtc);
            entity.HasIndex(t => t.SourceRef);
        });

        modelBuilder.Entity<CatalogQueryPlan>(entity =>
        {
            entity.ToTable("QueryPlan");
            entity.HasKey(p => p.PlanId);
            entity.Property(p => p.Sql).IsRequired();
            entity.Property(p => p.SourceRef).HasMaxLength(512).IsRequired();
            entity.Property(p => p.ProviderKind).HasMaxLength(16);
            entity.Property(p => p.Database).HasMaxLength(256);
            entity.Property(p => p.TargetPool).HasMaxLength(128);
            entity.Property(p => p.PreparedBy).HasMaxLength(256);
            // The sweep that clears lapsed plans, and the audit read of what one person prepared.
            entity.HasIndex(p => p.ExpiresUtc);
            entity.HasIndex(p => p.PreparedUtc);
        });

        modelBuilder.Entity<CatalogNotificationEvent>(entity =>
        {
            entity.ToTable("NotificationEvent");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.FlowName).HasMaxLength(400).IsRequired();
            entity.Property(e => e.FlowKind).HasMaxLength(16).IsRequired();
            // Error is nvarchar(max): the run's error text or the failed-assertion summary, no useful bound.
            // Detection inserts each (run, kind) at most once: the dedup that makes re-scanning the watermark's
            // overlap window (and any freak concurrent detection) free instead of a source of duplicate alerts.
            entity.HasIndex(e => new { e.RunId, e.Kind }).IsUnique();
            // Retention prunes by detection time.
            entity.HasIndex(e => e.DetectedUtc);
        });

        modelBuilder.Entity<CatalogNotificationSubscription>(entity =>
        {
            entity.ToTable("NotificationSubscription");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Channel).HasMaxLength(16).IsRequired();
            entity.Property(s => s.Mode).HasMaxLength(16).IsRequired();
            entity.Property(s => s.Kinds).HasMaxLength(128).IsRequired();
            entity.Property(s => s.FlowPattern).HasMaxLength(400);
            entity.Property(s => s.EmailAddress).HasMaxLength(320);
            entity.Property(s => s.SlackTarget).HasMaxLength(64);
            // The owner's self-service list.
            entity.HasIndex(s => s.UserId);
            // The dispatch scan: enabled subscriptions whose next-due has arrived (or is null = immediate).
            entity.HasIndex(s => new { s.Enabled, s.NextDueUtc });
        });

        modelBuilder.Entity<CatalogNotificationDelivery>(entity =>
        {
            entity.ToTable("NotificationDelivery");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Channel).HasMaxLength(16).IsRequired();
            entity.Property(d => d.Target).HasMaxLength(512).IsRequired();
            entity.Property(d => d.Subject).HasMaxLength(512).IsRequired();
            entity.Property(d => d.Status).HasMaxLength(16).IsRequired();
            // TextBody / HtmlBody / SlackBlocksJson / LastError are nvarchar(max): composed message content and
            // channel error payloads have no useful column bound.
            entity.Property(d => d.TextBody).IsRequired();
            // The send loop seeks queued deliveries whose next attempt is due, oldest first.
            entity.HasIndex(d => new { d.Status, d.NextAttemptUtc });
            // The owner's self-service history, newest first; CreatedUtc alone serves retention pruning.
            entity.HasIndex(d => new { d.UserId, d.CreatedUtc });
            entity.HasIndex(d => d.CreatedUtc);
        });

        modelBuilder.Entity<CatalogNotificationWatermark>(entity =>
        {
            entity.ToTable("NotificationWatermark");
            entity.HasKey(w => w.Id);
            // The single row's id is assigned by code (WellKnownId), never by the database.
            entity.Property(w => w.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<CatalogNotificationDigest>(entity =>
        {
            entity.ToTable("NotificationDigest");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Origin).HasMaxLength(16).IsRequired();
            entity.Property(d => d.Subject).HasMaxLength(512).IsRequired();
            // TextBody / HtmlBody / SlackBlocksJson / GroupsJson are nvarchar(max): rendered message content and
            // the persisted per-flow grouping have no useful column bound (both are capped in the composer).
            entity.Property(d => d.TextBody).IsRequired();
            entity.Property(d => d.HtmlBody).IsRequired();
            entity.Property(d => d.SlackBlocksJson).IsRequired();
            entity.Property(d => d.GroupsJson).IsRequired();
            // The GUI list (newest first) and the retention prune both read this column.
            entity.HasIndex(d => d.GeneratedUtc);
            // "The latest scheduled digest": the generator's self-healing check that a window is not re-covered.
            entity.HasIndex(d => new { d.Origin, d.PeriodEndUtc });
        });

        modelBuilder.Entity<CatalogChatConversation>(entity =>
        {
            entity.ToTable("ChatConversation");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Title).HasMaxLength(200).IsRequired();
            // The owner's conversation list, newest activity first.
            entity.HasIndex(c => new { c.UserId, c.UpdatedUtc });
        });

        modelBuilder.Entity<CatalogChatMessage>(entity =>
        {
            entity.ToTable("ChatMessage");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Role).HasMaxLength(16).IsRequired();
            // Text / ImagesJson / ToolCallsJson are nvarchar(max): an answer, a set of pasted
            // screenshots, or a long tool trail has no useful column bound.
            entity.Property(m => m.Text).IsRequired();
            // A transcript reads one conversation in emission order; unique because the appender
            // computes the next ordinal inside the save, so a duplicate is a bug surfaced early.
            entity.HasIndex(m => new { m.ConversationId, m.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<CatalogAccessToken>(entity =>
        {
            entity.ToTable("AccessToken");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            // SHA-256 hex is exactly 64 chars; bounded to that.
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Prefix).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Scopes).HasMaxLength(256).IsRequired();
            // Authentication looks a presented token up by its hash: unique (a hash collision would be a distinct
            // secret) and the seek index for every authenticated PAT request.
            entity.HasIndex(t => t.TokenHash).IsUnique();
            // The owner's token list (self-service management) seeks by user.
            entity.HasIndex(t => t.UserId);
        });
    }
}
