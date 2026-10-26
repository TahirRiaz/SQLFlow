using System.Globalization;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Maintenance;

/// <summary>
/// SQLFlow's own storage areas: every table of the catalog, grouped as the Maintenance page shows them, each with what it
/// holds, what maintenance may do with it and why. Two areas take a retention an admin sets (the SQL statements and the run
/// events of superseded runs); the rest are kept, or already clean themselves by a rule the area names. The ids are stored
/// with each area's retention and cleanup history, so they never change once shipped.
/// </summary>
public sealed class CatalogStorageContributor : IStorageContributor
{
    /// <summary>The SQL statements of superseded successful runs.</summary>
    public const string RunStatementsArea = "sqlflow.run-statements";

    /// <summary>The run events of superseded successful runs.</summary>
    public const string RunEventsArea = "sqlflow.run-events";

    private const string Schema = CatalogDbContext.SchemaName;

    private const string RunHistory = "Run history";
    private const string FlowsAndLineage = "Flows and lineage";
    private const string Fleet = "Nodes and work in progress";
    private const string Notifications = "Notifications";
    private const string Assistant = "Assistant and AI contexts";
    private const string Setup = "Users and settings";

    private const string RunTraceKeeps =
        "Each flow's latest run and every failed run keep theirs, whatever the days, and so does any run still queued or running.";

    private readonly IServiceProvider? _services;

    /// <summary>
    /// The contributor the control plane resolves per request or pass. It takes the catalog from <paramref name="services"/>
    /// only when it counts or cleans, so describing the areas (the startup check) never opens the catalog's connection.
    /// </summary>
    public CatalogStorageContributor(IServiceProvider services, IOptions<ControlPlaneOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The areas alone, described under <paramref name="options"/>, for a check that reads them without a database.</summary>
    public CatalogStorageContributor(ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Areas = Describe(options);
    }

    public string Owner => "SQLFlow";

    public IReadOnlyList<StorageArea> Areas { get; }

    public Task<long> CountAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        var catalog = Catalog();
        return area.Id switch
        {
            RunStatementsArea => RunTraceStore.CountPrunableStatementsAsync(catalog, olderThanUtc, ct),
            RunEventsArea => RunTraceStore.CountPrunableEventsAsync(catalog, olderThanUtc, ct),
            _ => throw NotCleanable(area),
        };
    }

    public Task<long> CleanAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        var catalog = Catalog();
        return area.Id switch
        {
            RunStatementsArea => RunTraceStore.PruneStatementsAsync(catalog, olderThanUtc, ct),
            RunEventsArea => RunTraceStore.PruneEventsAsync(catalog, olderThanUtc, ct),
            _ => throw NotCleanable(area),
        };
    }

    private CatalogDbContext Catalog()
        => _services?.GetRequiredService<CatalogDbContext>()
            ?? throw new InvalidOperationException("This storage contributor describes the catalog's areas only; resolve it from the control plane's services to clean them.");

    private static InvalidOperationException NotCleanable(StorageArea area)
        => new($"Storage area '{area.Id}' takes no retention, so nothing cleans it from the Maintenance page.");

    private static StorageTable T(string name) => StorageTable.Named(Schema, name);

    private static string Days(int days) => days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days");

    private static IReadOnlyList<StorageArea> Describe(ControlPlaneOptions options)
    {
        var notifications = options.Notifications;
        string NotificationLifecycle(int days, string setting, string after)
            => notifications.Enabled
                ? $"Removed automatically {Days(days)} after {after} (ControlPlane:Notifications:{setting})."
                : "Nothing writes or removes it while notifications are off (ControlPlane:Notifications:Enabled).";

        string NotificationBrief(int days) => notifications.Enabled ? $"After {Days(days)}" : "Notifications are off";

        var nodeHours = options.Dispatch.NodeRetentionHours;
        var nodeBrief = nodeHours > 0 ? $"Offline for {nodeHours.ToString(CultureInfo.InvariantCulture)} hours" : "From the Nodes page";
        var nodeLifecycle = nodeHours > 0
            ? $"A node offline for {nodeHours.ToString(CultureInfo.InvariantCulture)} hours is removed automatically (ControlPlane:Dispatch:NodeRetentionHours); the Nodes page removes one at once."
            : "Kept: ControlPlane:Dispatch:NodeRetentionHours is 0, so offline nodes are removed only from the Nodes page.";

        return
        [
            new()
            {
                Id = "sqlflow.runs",
                Group = RunHistory,
                Title = "Runs",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per run and per run group: what ran, when, who asked, the outcome and error, and the rows it loaded, inserted, updated and deleted.",
                Reason = "This is the run history and its statistics. The Runs board, every run's page, Insights, Data streams and the schedules read it, and it is small next to what each run's detail takes.",
                Brief = "Deleted with its repository",
                Lifecycle = "Deleted only with its repository.",
                Grows = true,
                Tables = [T("Run"), T("RunGroup")],
                ManagedAt = "/runs",
            },
            new()
            {
                Id = "sqlflow.trigger-requests",
                Group = RunHistory,
                Title = "Trigger requests",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per request to sync a repository and fire its schedules: who asked and from which workflow run, the commits asked for and synced, each schedule's fire and the runs it started.",
                Reason = "It is the record that ties a merge, the sync it caused and the runs it fired together; the External triggers page and each caller's job read it.",
                Brief = "Never removed",
                Lifecycle = "Nothing removes them; a request is a few rows, and its trace is one activity.",
                Grows = true,
                Tables = [T("TriggerRequest"), T("TriggerRequestSchedule"), T("TriggerRequestRun")],
                ManagedAt = "/external-triggers",
            },
            new()
            {
                Id = "sqlflow.run-details",
                Group = RunHistory,
                Title = "Run files and results",
                Verdict = StorageVerdict.Keep,
                Holds = "Per run: each file it processed, each assertion it checked, each surrogate key it assigned and each health metric it measured.",
                Reason = "A run's Files, Assertions, Surrogate keys and Health metrics tabs read them, and so do the file history (which runs handled a file) and the assertion notifications.",
                Brief = "Deleted with its repository",
                Lifecycle = "Deleted only with its repository.",
                Grows = true,
                Tables = [T("RunFile"), T("RunAssertion"), T("RunSurrogateKey"), T("RunHealthCheckMetric")],
                ManagedAt = "/runs",
            },
            new()
            {
                Id = RunStatementsArea,
                Group = RunHistory,
                Title = "SQL statements",
                Verdict = StorageVerdict.Safe,
                Holds = "The SQL each run generated, one row per statement, with the statement's error when it failed.",
                Reason = "A flow generates nearly the same SQL on every run; it changes only when a table's schema does, and schema changes are recorded on their own. So an older successful run's copy duplicates its flow's latest run, and removing it loses nothing. The run itself, its totals and its error stay.",
                Retention = new StorageRetention(RunTraceKeeps, "statements", MinDays: 0),
                Tables = [T("RunStatement")],
                ManagedAt = "/runs",
            },
            new()
            {
                Id = RunEventsArea,
                Group = RunHistory,
                Title = "Run events",
                Verdict = StorageVerdict.Optional,
                Holds = "Each run's log: one row per event, with the rows each step affected and how long it took.",
                Reason = "Removing a run's events removes its Events tab and its steps from Insights' step timings. The run's totals (rows loaded, duration, outcome) stay on the run, so Data streams and run history keep their statistics.",
                Retention = new StorageRetention(RunTraceKeeps, "events", MinDays: 0),
                Tables = [T("RunEvent")],
                ManagedAt = "/insights",
            },
            new()
            {
                Id = "sqlflow.flow-versions",
                Group = RunHistory,
                Title = "Flow versions",
                Verdict = StorageVerdict.Keep,
                Holds = "Each distinct flow document a run was queued with, stored once per content.",
                Reason = "A node fetches the exact document its run was queued with from here, and a run's page shows what it ran.",
                Brief = "One per distinct document",
                Lifecycle = "Never removed; one row per distinct document, so it grows only when flows change.",
                Tables = [T("FlowVersion")],
            },
            new()
            {
                Id = "sqlflow.repositories",
                Group = FlowsAndLineage,
                Title = "Repositories and flows",
                Verdict = StorageVerdict.Keep,
                Holds = "The repositories and their sources, the flows each declares, their columns and the dependencies between them.",
                Reason = "This is the catalog's copy of what the repositories hold, which every page and every run starts from.",
                Brief = "Rewritten by each sync",
                Lifecycle = "Rewritten by each repository sync; deleted with its repository.",
                Tables = [T("Repo"), T("RepoSource"), T("Pipeline"), T("PipelineColumn"), T("FlowDependency")],
                ManagedAt = "/repos",
            },
            new()
            {
                Id = "sqlflow.lineage",
                Group = FlowsAndLineage,
                Title = "Objects and lineage",
                Verdict = StorageVerdict.Keep,
                Holds = "The database objects the flows read and write, their columns, and the lineage edges and relationships between them.",
                Reason = "The catalog, lineage and search pages read them. They describe the estate as it is now.",
                Brief = "Rewritten by each sync",
                Lifecycle = "Rewritten by each repository sync; objects nothing knows any more are forgotten by the sync.",
                Tables = [T("Object"), T("ObjectColumn"), T("ObjectRelationship"), T("LineageEdge")],
                ManagedAt = "/lineage",
            },
            new()
            {
                Id = "sqlflow.schema-changes",
                Group = FlowsAndLineage,
                Title = "Schema changes",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per difference a source-control run found in a database object.",
                Reason = "It is the history of every table's changes; a history that forgets is not a history.",
                Brief = "Deleted with its repository",
                Lifecycle = "Deleted only with its repository.",
                Grows = true,
                Tables = [T("SchemaChange")],
                ManagedAt = "/schema-changes",
            },
            new()
            {
                Id = "sqlflow.schedules",
                Group = FlowsAndLineage,
                Title = "Schedules",
                Verdict = StorageVerdict.Keep,
                Holds = "The schedules, their members and the schedules each one waits for.",
                Reason = "The scheduler fires from them.",
                Brief = "Rewritten by each sync",
                Lifecycle = "Rewritten by each repository sync; deleted with its repository.",
                Tables = [T("Schedule"), T("ScheduleMember"), T("ScheduleParent")],
            },
            new()
            {
                Id = "sqlflow.subscribers",
                Group = FlowsAndLineage,
                Title = "Subscribers",
                Verdict = StorageVerdict.Keep,
                Holds = "Who reads the warehouse: the subscribers, their queries, their reports' pages, visuals and fields, their models, and the questions their reports answer.",
                Reason = "The Subscribers page and the assistant's answers about reports read them.",
                Brief = "Rewritten by each sync",
                Lifecycle = "Rewritten by each repository sync; deleted with its repository.",
                Tables =
                [
                    T("Subscriber"), T("SubscriberQuery"), T("SubscriberReportPage"), T("SubscriberReportVisual"),
                    T("SubscriberReportField"), T("SubscriberReportVisualQuestion"), T("SubscriberModelTable"),
                    T("SubscriberModelField"), T("SubscriberModelRelationship"),
                ],
                ManagedAt = "/subscribers",
            },
            new()
            {
                Id = "sqlflow.activity-traces",
                Group = Fleet,
                Title = "Sync and discovery traces",
                Verdict = StorageVerdict.Safe,
                Holds = "The log lines of repository syncs, source discoveries and trigger requests, which the trace panel shows.",
                Reason = "Only the latest attempts are worth reading; an old sync's lines describe a state long replaced. A trigger request copies the lines of the sync it waited for into its own trace.",
                Brief = "Last 5 attempts per subject",
                Lifecycle = "Removed automatically: each subject keeps the lines of its last five attempts, a discovery's lines go after 2 days, and a repository source's go with it. A trigger request has one trace, kept as long as the request.",
                Tables = [T("ActivityEvent")],
            },
            new()
            {
                Id = "sqlflow.nodes",
                Group = Fleet,
                Title = "Nodes",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per node that has polled the control plane, with its last heartbeat and what it reported.",
                Reason = "A node that has gone away for good tells nothing; every worker replica that scales in leaves one behind.",
                Brief = nodeBrief,
                Lifecycle = nodeLifecycle,
                Tables = [T("Node")],
                ManagedAt = "/nodes",
            },
            new()
            {
                Id = "sqlflow.dispatch",
                Group = Fleet,
                Title = "Dispatch and worker pools",
                Verdict = StorageVerdict.Keep,
                Holds = "The dispatcher's lease and each worker pool's desired size.",
                Reason = "The dispatcher and the autoscaler read them; a handful of rows.",
                Brief = "Updated in place",
                Lifecycle = "Updated in place; never grows.",
                Tables = [T("DispatchLease"), T("WorkerPool")],
            },
            new()
            {
                Id = "sqlflow.compute-tasks",
                Group = Fleet,
                Title = "Compute tasks",
                Verdict = StorageVerdict.Keep,
                Holds = "One row per data source task a node ran (a key detection, a query), with its result.",
                Reason = "Key detection, query results and the warehouse panel read them.",
                Brief = "Never removed",
                Lifecycle = "Nothing removes them; a task that no worker finished is marked failed, not removed.",
                Grows = true,
                Tables = [T("ComputeTask")],
                ManagedAt = "/key-detection",
            },
            new()
            {
                Id = "sqlflow.query-plans",
                Group = Fleet,
                Title = "Prepared queries",
                Verdict = StorageVerdict.Safe,
                Holds = "Queries prepared for approval before they run, each valid for 15 minutes.",
                Reason = "An expired plan can no longer be run.",
                Brief = "After 24 hours",
                Lifecycle = "Removed automatically 24 hours after it was prepared, when the next query is prepared.",
                Tables = [T("QueryPlan")],
            },
            new()
            {
                Id = "sqlflow.notification-events",
                Group = Notifications,
                Title = "Notification events",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per failed, cancelled or skipped run and failed assertion, waiting to be sent to whoever subscribed.",
                Reason = "An event is sent within minutes; once sent, the run itself holds what happened.",
                Brief = NotificationBrief(notifications.EventRetentionDays),
                Lifecycle = NotificationLifecycle(notifications.EventRetentionDays, "EventRetentionDays", "it was raised"),
                Tables = [T("NotificationEvent")],
                ManagedAt = "/settings/notifications",
            },
            new()
            {
                Id = "sqlflow.notification-deliveries",
                Group = Notifications,
                Title = "Sent notifications",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per message sent or tried, with its channel, target and outcome.",
                Reason = "The delivery history shows recent messages; an old message is not acted on.",
                Brief = NotificationBrief(notifications.DeliveryRetentionDays),
                Lifecycle = NotificationLifecycle(notifications.DeliveryRetentionDays, "DeliveryRetentionDays", "it was sent"),
                Tables = [T("NotificationDelivery")],
                ManagedAt = "/settings/notifications",
            },
            new()
            {
                Id = "sqlflow.notification-digests",
                Group = Notifications,
                Title = "Digests",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per estate digest generated, scheduled or asked for.",
                Reason = "A digest summarises a period that has passed; the runs it summarised stay.",
                Brief = NotificationBrief(notifications.DigestRetentionDays),
                Lifecycle = NotificationLifecycle(notifications.DigestRetentionDays, "DigestRetentionDays", "it was generated"),
                Tables = [T("NotificationDigest")],
                ManagedAt = "/settings/notifications",
            },
            new()
            {
                Id = "sqlflow.notification-setup",
                Group = Notifications,
                Title = "Subscriptions",
                Verdict = StorageVerdict.Keep,
                Holds = "Who subscribed to which notifications, and how far detection has read.",
                Reason = "Notifications are sent from them.",
                Brief = "Each person's own",
                Lifecycle = "Each person manages their own; removed with the user.",
                Tables = [T("NotificationSubscription"), T("NotificationWatermark")],
                ManagedAt = "/settings/notifications",
            },
            new()
            {
                Id = "sqlflow.chats",
                Group = Assistant,
                Title = "Chat conversations",
                Verdict = StorageVerdict.Keep,
                Holds = "Each person's conversations with the assistant and their messages, images included.",
                Reason = "They belong to the person who had them.",
                Brief = "Each person deletes their own",
                Lifecycle = "Each person deletes their own on the Chat page; removed with the user.",
                Grows = true,
                Tables = [T("ChatConversation"), T("ChatMessage")],
                ManagedAt = "/chat",
            },
            new()
            {
                Id = "sqlflow.ai-contexts",
                Group = Assistant,
                Title = "AI contexts and saved answers",
                Verdict = StorageVerdict.Keep,
                Holds = "The approved AI contexts, their column policies, objects, relationships and measures, the report specs, and the answers people saved as examples.",
                Reason = "The assistant answers from them; an admin approves and removes each.",
                Brief = "Removed by an admin",
                Lifecycle = "Removed by an admin on the AI context page, or with the repository that proposed it.",
                Tables =
                [
                    T("AiContext"), T("ColumnPolicy"), T("SemanticObject"), T("SemanticRelationship"), T("SemanticMeasure"),
                    T("SemanticLayerSettings"), T("SemanticReportSpec"), T("SemanticExample"),
                ],
                ManagedAt = "/semantic-layer",
            },
            new()
            {
                Id = "sqlflow.users",
                Group = Setup,
                Title = "Users and access tokens",
                Verdict = StorageVerdict.Keep,
                Holds = "The users, their roles and their personal access tokens.",
                Reason = "Sign-in and every authorization read them.",
                Brief = "Managed on the Users page",
                Lifecycle = "Managed on the Users page; a token is revoked rather than removed, and goes with its user.",
                Tables = [T("User"), T("Role"), T("AccessToken")],
                ManagedAt = "/users",
            },
            new()
            {
                Id = "sqlflow.external-triggers",
                Group = Setup,
                Title = "External triggers",
                Verdict = StorageVerdict.Keep,
                Holds = "The GitHub workflows and key holders that may sync a repository and fire its schedules, with what each may fire and when it was last used.",
                Reason = "Every exchange and every trigger request checks them.",
                Brief = "Managed on its page",
                Lifecycle = "Managed on the External triggers page; one the configuration names is written at every start.",
                Tables = [T("ExternalTrigger")],
                ManagedAt = "/external-triggers",
            },
            new()
            {
                Id = "sqlflow.retention-settings",
                Group = Setup,
                Title = "Retention settings",
                Verdict = StorageVerdict.Keep,
                Holds = "The retention an admin set for each area on this page, with who set it and when.",
                Reason = "The automatic cleanup applies them.",
                Brief = "One row per area",
                Lifecycle = "One row per area; never grows.",
                Tables = [T("RetentionSetting")],
                ManagedAt = "/settings/maintenance",
            },
            new()
            {
                Id = "sqlflow.cleanup-log",
                Group = Setup,
                Title = "Cleanup log",
                Verdict = StorageVerdict.Safe,
                Holds = "One row per cleanup that removed something, failed or was asked for: who, when, at which cut-off and how many rows went.",
                Reason = "A year of it answers what maintenance removed; older entries describe cleanups nobody will ask about.",
                Brief = "After a year",
                Lifecycle = "Removed automatically a year after the cleanup.",
                Tables = [T("MaintenanceCleanup")],
                ManagedAt = "/settings/maintenance",
            },
            new()
            {
                Id = "sqlflow.schema-history",
                Group = Setup,
                Title = "Catalog schema history",
                Verdict = StorageVerdict.Keep,
                Holds = "The catalog migrations applied to this database.",
                Reason = "The control plane checks the database against the build from it.",
                Brief = "Never removed",
                Lifecycle = "One row per migration; never removed.",
                Tables = [T(CatalogDatabase.MigrationsHistoryTable)],
            },
        ];
    }
}
