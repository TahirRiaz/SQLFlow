// The control plane API contracts, mirrored by hand from src/SqlFlow.ControlPlane/Api/Contracts.cs and the
// endpoint DTOs. Property names are the camelCase form System.Text.Json emits. All timestamps are UTC and may
// arrive without a timezone suffix; parse them with parseUtc from ../lib/time.

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

// ---- Authentication ---------------------------------------------------------------------------------------------

export interface SessionResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  subject: string;
  role: string;
  scopes: string[];
}

export interface TokenResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
}

export interface EntraProviderInfo {
  enabled: boolean;
  clientId: string | null;
  authority: string | null;
}

export interface AuthProviders {
  local: boolean;
  bootstrap: boolean;
  entra: EntraProviderInfo;
}

// ---- Dashboard ----------------------------------------------------------------------------------------------------

export interface RunCounts {
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  last24h: number;
}

export interface Dashboard {
  repos: number;
  pipelines: number;
  activePipelines: number;
  runs: RunCounts;
  nodesOnline: number;
  nodesTotal: number;
  schedulesEnabled: number;
  schedulesPaused: number;
  repoSources: number;
  repoSourcesWithErrors: number;
  asOfUtc: string;
}

// ---- Catalog --------------------------------------------------------------------------------------------------------

export interface Repo {
  id: string;
  name: string;
  remoteUrl: string | null;
  rootPath: string | null;
  firstSeenUtc: string;
  lastSyncUtc: string;
}

/** One entry in a repository's content listing: a repo-relative, forward-slashed path, whether it is a folder, and
 * the file's size in bytes (0 for a folder). */
export interface RepoTreeEntry {
  path: string;
  isFolder: boolean;
  sizeBytes: number;
}

/** Everything a repository holds, path-ordered: the folders and files on the synced branch (or on disk for a
 * local-path repo), not only the flow files the catalog imported. `readFrom` is "git" or "disk"; `truncated` says the
 * listing hit its server-side cap and is partial. */
export interface RepoTree {
  readFrom: string;
  entries: RepoTreeEntry[];
  truncated: boolean;
}

/** The outcome of a manual local-path repo sync: the pipeline reconciliation counts and lineage tallies, whether the
 * derived tier connected to the live database, and any warnings the pass surfaced (bounded). */
export interface RepoSyncResult {
  pipelinesAdded: number;
  pipelinesUpdated: number;
  pipelinesUnchanged: number;
  pipelinesDeactivated: number;
  pipelinesDeleted: number;
  objects: number;
  columns: number;
  edges: number;
  waves: number;
  dependencies: number;
  connected: boolean;
  warnings: string[];
}

/** What a repo deletion removed: the counts of the repo-scoped rows purged, and whether a managed git source
 * registered under the same name was dropped too (so the repo cannot resurrect on the next background sync). */
export interface RepoDeletionResult {
  pipelines: number;
  runs: number;
  runGroups: number;
  schedules: number;
  lineageEdges: number;
  sourceRemoved: boolean;
}

export interface PipelineSummary {
  id: string;
  repoId: string;
  name: string;
  kind: string;
  batch: string | null;
  wave: number;
  active: boolean;
  /** "auto" (default: schedules and group runs execute it) or "manual" (the flow's YAML mode: it runs only
   * when triggered directly; the scheduler and batch/node expansion skip it). */
  executionMode: "auto" | "manual" | "disabled";
  /** "production" (default) or "development" (the flow's YAML lifecycle:); development flows run normally
   * but never generate notification events. */
  lifecycle: string;
  sourceServer: string | null;
  targetServer: string | null;
  relativePath: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

/** One batch (source-system grouping) of a repo's flows with its flow counts; a flow that declares no batch
 * reports under the "default" batch. */
export interface PipelineBatch {
  repoId: string;
  batch: string;
  flowCount: number;
  activeCount: number;
}

export interface PipelineDetail extends PipelineSummary {
  contentHash: string;
  yaml: string;
  definitionJson: string;
}

/** One resolved column of a pipeline's pre-ingestion transformation view: "declared" rows come from the flow
 * YAML (the source of truth), "detected" rows from the latest run's generated view. */
export interface PipelineColumn {
  kind: "declared" | "detected";
  ordinal: number;
  columnName: string;
  sourceColumn: string | null;
  expression: string | null;
  dataType: string | null;
  sortOrder: number | null;
  isVirtual: boolean;
  excludeFromView: boolean;
  converted: boolean;
}

// ---- Runs -----------------------------------------------------------------------------------------------------------

export type RunStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "skipped";

/** How a run was scoped: one flow, a flow and its descendants (Node), or a whole batch / data source. */
export type RunScope = "flow" | "node" | "batch";

export interface RunSummary {
  runId: string;
  pipelineId: string;
  repoId: string | null;
  flowName: string;
  flowKind: string;
  /** The batch label joined from the pipeline row; a flow with no YAML batch reports under "default". */
  batch: string;
  /** The lineage execution step (wave) joined from the pipeline row; -1 until lineage has been computed. */
  wave: number;
  status: RunStatus;
  success: boolean;
  targetPool: string | null;
  commitSha: string | null;
  writtenUtc: string;
  enqueuedUtc: string | null;
  durationSeconds: number | null;
  rowsLoaded: number | null;
  rowsInserted: number | null;
  rowsUpdated: number | null;
  rowsDeleted: number | null;
  /** How many source files this run processed (file flows); 0 for non-file flows. */
  fileCount: number;
  /** The run group this run belongs to when it was launched as one member of a Node or Batch run; null for a
   * standalone single-flow run. */
  groupId: string | null;
  /** The run's newest trace event (a stage summary, a file read, a decision): what the run is doing right now
   * while it executes, what it did last once it ended. Null when no events were recorded (a queued run, or one
   * that predates the event stream). */
  lastAction: string | null;
  lastActionUtc: string | null;
  /** Why a failed run failed, carried on the summary so a set (a schedule's fire, a batch run) can show its
   * failures where they happened. Null for every run that did not fail. */
  error: string | null;
}

export interface RunDetail extends RunSummary {
  claimedByNode: string | null;
  /** When an operator asked to cancel this run while it was already running; null otherwise. While set and the run
   * is still "running", the owning node is aborting the in-flight statement (a transitional "cancelling" state). */
  cancelRequestedUtc: string | null;
  schemaVersion: number;
  startUtc: string | null;
  endUtc: string | null;
  error: string | null;
  host: string | null;
  fullLoad: boolean;
  backfillFrom: string | null;
  backfillTo: string | null;
  filePattern: string | null;
  /** The raw predicate this run appended to the source read, in the source's own dialect, or null. */
  sourceFilter: string | null;
  /** True when this run evaluated the flow's data-quality assertions (manual-mode ones included) against the
   * current target without loading anything (the on-demand assertion run). */
  assertionsOnly: boolean;
  /** The incremental read scope the engine computed and applied this run: mode (full / incremental / backfill /
   * init-load), the filter that bounded the read, and the resolved watermark with the object it was probed from.
   * Null on flows with no incremental surface. Distinct from the operator's backfill parameters above. */
  incrementalMode: string | null;
  incrementalFilter: string | null;
  incrementalWatermark: string | null;
  incrementalWatermarkSource: string | null;
  /** How DataSet_DW was derived for a file run (e.g. "filename dates; month-first (inferred from file set)" or
   * "last-modified"), so the detail view shows what the reader detected. Null for flows with no DataSet_DW. */
  dataSetConvention: string | null;
  /** On a failed run, the exact statement that threw (the run's failure point), so the detail view can show the
   * offending SQL next to the error banner. Null on every non-failed run, or when no statement was attributed. */
  failedStatementOrdinal: number | null;
  failedStatementStep: string | null;
  failedStatementSql: string | null;
}

export interface RunFile {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  path: string | null;
  rows: number;
  columns: number;
  sizeBytes: number;
  hash: string | null;
}

/** The newest files a pipeline has processed, profiled on their own: the current delivery shape, which reads
 * differently from the lifetime profile when a source's files have grown or shrunk. */
export interface PipelineRecentFiles {
  fileCount: number;
  avgBytes: number;
  minBytes: number;
  maxBytes: number;
  oldestModified: string | null;
  newestModified: string | null;
}

/** The size profile of a pipeline's file deliveries, computed over every distinct file in its run history:
 * what a normal delivery from this flow looks like. A pipeline that has processed no files reports zeros. */
export interface PipelineFileStats {
  fileCount: number;
  totalBytes: number;
  avgBytes: number;
  /** The median size: the honest "typical file" when a few outsized deliveries drag the mean. */
  medianBytes: number;
  minBytes: number;
  maxBytes: number;
  /** Population standard deviation: how much variation is normal before a file counts as anomalous. */
  stdDevBytes: number;
  totalRows: number;
  avgRows: number;
  oldestModified: string | null;
  newestModified: string | null;
  recent: PipelineRecentFiles | null;
}

/** One object a source-control snapshot found added, changed, or dropped in a managed database. */
export interface SchemaChange {
  id: number;
  repoId: string;
  runId: string;
  pipelineId: string | null;
  database: string;
  category: string;
  schema: string | null;
  name: string;
  changeType: "Added" | "Changed" | "Deleted";
  commitSha: string | null;
  occurredUtc: string;
}

/** One commit in the snapshot repository, as an endpoint of a comparison. */
export interface GitRevision {
  sha: string;
  shortSha: string;
  authorName: string;
  committedUtc: string;
  message: string;
}

/**
 * One database object's DDL at the two ends of a window: what the snapshot repository held before the window
 * opened, and what it holds now. `before` is null when the history itself begins inside the window; a null
 * text on either side means the object's file was absent at that revision, so no `beforeText` is an object
 * added during the window and no `afterText` one dropped in it.
 */
export interface SchemaObjectCompare {
  path: string;
  before: GitRevision | null;
  after: GitRevision;
  beforeText: string | null;
  afterText: string | null;
  linesAdded: number;
  linesDeleted: number;
  /** At least one side was cut at the server's inline limit, so it is not the whole script. */
  truncated: boolean;
}

/** One tracked database's change tally over the requested window. */
export interface SchemaChangeDatabase {
  database: string;
  total: number;
  added: number;
  changed: number;
  deleted: number;
  lastChangeUtc: string;
}

export interface PipelineFile {
  name: string;
  path: string | null;
  modified: string | null;
  rows: number;
  sizeBytes: number;
  lastRun: boolean;
  lastProcessedUtc: string | null;
}

export interface RunAssertion {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  result: string;
  assertedValue: string;
  evaluated: boolean;
  error: string | null;
}

export interface RunStatement {
  id: number;
  runId: string;
  repoId: string | null;
  ordinal: number;
  /** When the statement was generated (UTC); null on statements recorded before the trace carried timestamps. */
  timestampUtc: string | null;
  step: string;
  sql: string;
  /** The error this statement raised, or null when it succeeded (or was never reached). Set on exactly one
   * statement of a failed run: the one whose execution threw. */
  error: string | null;
}

/** One entry of a run's consolidated trace: a canonical run event (kind "event": file progress, a resolved
 * watermark, an engine decision, a stage summary, a warning) or a generated SQL statement (kind "statement"),
 * the two streams interleaved by timestamp. An event carries message (plus optional rows/elapsedMs); a
 * statement carries sql (plus error on the one that threw). */
export interface RunTraceEntry {
  id: number;
  runId: string;
  repoId: string | null;
  kind: "event" | "statement";
  /** 1-based position within the entry's own stream (events and statements count separately). */
  ordinal: number;
  /** Null only on statements recorded before the trace carried timestamps; those sort first. */
  timestampUtc: string | null;
  level: "trace" | "debug" | "info" | "warning" | "error";
  step: string | null;
  message: string | null;
  sql: string | null;
  error: string | null;
  rows: number | null;
  elapsedMs: number | null;
}

/** One entry of a control-plane activity trace (a repository sync, a lineage computation, ...): the append-only,
 * id-cursored log the bottom trace panel tails for one (kind, subject), the general-purpose twin of a run's trace.
 * `terminal` marks the activity's final line and `status` its outcome ("succeeded"/"failed"); every other line
 * leaves `status` null. */
export interface ActivityEvent {
  id: number;
  activityId: string;
  kind: string;
  subjectKey: string;
  /** 1-based position within the activity (emission order). */
  ordinal: number;
  timestampUtc: string;
  level: "trace" | "debug" | "info" | "warning" | "error";
  step: string | null;
  message: string | null;
  terminal: boolean;
  status: string | null;
}

export interface RunSurrogateKey {
  id: number;
  runId: string;
  repoId: string | null;
  surrogateKeyId: number;
  surrogateTable: string;
  surrogateColumn: string;
  isRemote: boolean;
  keysGenerated: number;
  rowsStamped: number;
  executed: boolean;
  error: string | null;
}

export interface RunHealthCheckMetric {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  seriesPoints: number;
  imputedPoints: number;
  immaturePoints: number;
  anomalies: number;
  levelShifts: number;
  modelTrained: boolean;
  modelTrainer: string | null;
  error: string | null;
}

export type RunParameterInput = "Toggle" | "DateRange" | "Glob" | "SqlPredicate";

/** One run parameter that applies to a flow, with the metadata the trigger form renders from. `key` maps back to
 *  the trigger request: "fullLoad", "backfillWindow" (backfillFrom/backfillTo), "filePattern", "sourceFilter",
 *  "assertionsOnly". */
export interface RunParameterDescriptor {
  key: string;
  input: RunParameterInput;
  label: string;
  help: string;
}

/** The run parameters that apply to a pipeline, driven by its kind and definition (the same per-kind rules the
 *  engine honors). `parameters` is empty for kinds with no selection surface (stored procedure, health check,
 *  inventory), so the trigger form shows only controls the run will actually honor. */
export interface FlowParameters {
  flowKind: string;
  parameters: RunParameterDescriptor[];
}

export interface RunTriggerRequest {
  repoId: string;
  flowName: string;
  pool?: string | null;
  commitSha?: string | null;
  // The built-in backfill: per-run substitution parameters, all optional and audited on the run. Honored only for
  // a single flow (scope "flow" or omitted); a Node or Batch run always runs its members with default parameters.
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
  /** An extra predicate ANDed onto the source read for this run, in the SOURCE's SQL dialect and starting with
   * AND, e.g. "AND pk > 92992". Replaces the incremental watermark for the run, so it can reach rows already
   * below the high-water mark, and needs no declared date column. Relational ingestion only. */
  sourceFilter?: string | null;
  /** Evaluate the flow's data-quality assertions (manual-mode ones included) against the current target and
   * load nothing. Ingestion flows only; single-flow scope only. */
  assertionsOnly?: boolean;
  /** The execution scope: one flow (default), a flow and its descendants (node), or a whole batch (batch). */
  scope?: RunScope;
  /** Node scope's "find all": include mode: manual and mode: disabled descendants in the group. False (the
   * default) runs only the active (mode: auto) descendants. */
  includeAll?: boolean;
  /** The batch label for a batch-scoped run; when omitted the anchor flow's own batch is used. */
  batch?: string | null;
}

/** The trigger response covers both a single flow (runId set) and a Node/Batch group (groupId + memberCount set). */
export interface RunTriggerAccepted {
  runId?: string | null;
  groupId?: string | null;
  memberCount?: number | null;
  status: string;
}

/** One flow a scope expansion would run, with the wave that orders it. */
export interface RunScopePreviewMember {
  flowName: string;
  flowKind: string;
  wave: number;
}

/** What a Node or Batch run would enqueue, without enqueuing: the resolved anchor and the ordered members. */
export interface RunScopePreview {
  scope: RunScope;
  anchor: string;
  memberCount: number;
  waveCount: number;
  members: RunScopePreviewMember[];
}

/** A run group's member counts by lifecycle state. */
export interface RunGroupCounts {
  total: number;
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  skipped: number;
}

/** One run group's header and the live rollup of its members. The members come from the runs list by groupId. */
export interface RunGroup {
  groupId: string;
  repoId: string;
  mode: "node" | "batch";
  anchor: string;
  memberCount: number;
  commitSha: string | null;
  enqueuedUtc: string;
  counts: RunGroupCounts;
}

// ---- Schedules -------------------------------------------------------------------------------------------------------

export interface Schedule {
  id: string;
  repoId: string;
  /** The schedule's name: what a flow joins with `schedule: <name>`, and its identity within the repo. What a fire
   * runs is its MEMBER SET (the flows that joined it), enqueued as one wave-ordered run group. */
  name: string;
  /** The pipeline ids of the flows that joined this schedule: exactly what a fire runs, wave-ordered. */
  memberPipelineIds: string[];
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  /** Whether missed occurrences are backfilled (one per scheduler tick) instead of skipped. */
  catchup: boolean;
  /** How many member flows one fire executes at once; null means unbounded. Because a group's waves are gated this
   * is the width of the running wave, so it caps the load a fan-out puts on the source the members share. */
  maxConcurrency: number | null;
  paused: boolean;
  source: string;
  nextFireUtc: string | null;
  lastFireUtc: string | null;
  lastRunId: string | null;
  /** The run group the last fire enqueued, when the schedule has more than one member; null for a single-member
   * schedule (which enqueues one run) or one that has never fired. */
  lastGroupId: string | null;
  /** True while that last group is still executing (a member is queued or running), so the list can offer a live
   * re-entry point back to the running set. Always false for a single-member schedule, which has no group. */
  lastGroupActive: boolean;
  /** How the last fire ended: its members tallied by lifecycle state (the single run's own state when the fire ran
   * one flow). Null when the schedule has never fired, or its runs have aged out of the catalog. */
  lastCounts: RunGroupCounts | null;
  /** The schedule this one CHAINS BEHIND, or null when it is driven by the clock. A chained schedule has no cadence
   * of its own and a null `nextFireUtc`: it becomes due once, when the named parent's fire completes. */
  afterSchedule?: string | null;
  /** The schedules this one sets off when it finishes, in chain order. Firing the head of a five-link chain
   * dispatches all five, so a run dialog showing only this schedule's own members would understate it. */
  triggersSchedules?: ScheduleChainLink[] | null;
  createdUtc: string;
  updatedUtc: string;
}

/** One link a schedule sets off. `enabled`/`paused` matter to an operator about to start the head: a stopped link
 * ends the chain there, and the tail never runs. */
export interface ScheduleChainLink {
  id: string;
  name: string;
  /** How far down the chain this link sits: 1 is fired directly by this schedule, 2 by that one, and so on. */
  depth: number;
  memberCount: number;
  enabled: boolean;
  paused: boolean;
}

/** The YAML behind a schedule: the file git declares its cadence in, and that file's text. `yaml` is null for an
 * API-created schedule (no file backs it) and for a git schedule whose declaring flow has left the estate. */
export interface ScheduleDefinition {
  scheduleId: string;
  name: string;
  source: string;
  /** The repo-relative path of the declaring file. */
  path: string | null;
  /** The flow whose inline `schedule:` block declares it; null when a schedules.yaml library file does. */
  flowName: string | null;
  /** That flow's pipeline id, so the definition can link to the flow it lives on. */
  pipelineId: string | null;
  yaml: string | null;
}

export interface CreateScheduleRequest {
  repoId: string;
  /** The flow names that join this schedule: membership is what a fire runs, so at least one is required. */
  members: string[];
  cron?: string | null;
  intervalSeconds?: number | null;
  timezone?: string | null;
  enabled?: boolean | null;
  catchup?: boolean | null;
  /** What other flows would join with `schedule: <name>`. Defaults to the first member's flow name. */
  name?: string | null;
  /** How many members one fire runs at once. Omit for the product default (4); 0 asks for unbounded. */
  maxConcurrency?: number | null;
}

export interface ScheduleCreated {
  id: string;
  nextFireUtc: string | null;
}

/** The manual run-now acknowledgement: the run the fire enqueued (the group's first member for a scoped schedule),
 * plus the run group and member count when the scope expanded to a wave-ordered set. */
export interface ScheduleRunAccepted {
  runId: string;
  groupId: string | null;
  memberCount: number;
}

/** One flow a schedule runs: the wave that orders it within a fire, and the batch it carries (coalesced to the
 * default batch when the flow declares none) so the run board can group or filter the plan by batch. */
export interface SchedulePlanMember {
  flowName: string;
  flowKind: string;
  wave: number;
  batch: string;
  /** The flow itself, so a plan row can link to it; null when the expansion did not resolve its pipeline row. */
  pipelineId: string | null;
}

/** When a schedule next runs and exactly what it executes: the cadence plus the lineage-resolved flows in wave
 * order. Members sharing a wave run concurrently; a wave starts only once the previous one is terminal. This is
 * what a fire (automatic or run-now) will enqueue, so it drives the pre-flight run board. */
export interface SchedulePlan {
  scheduleId: string;
  repoId: string;
  name: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  paused: boolean;
  nextFireUtc: string | null;
  lastFireUtc: string | null;
  anchor: string;
  memberCount: number;
  waveCount: number;
  members: SchedulePlanMember[];
}

// ---- Nodes ------------------------------------------------------------------------------------------------------------

export interface Node {
  name: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  version: string | null;
  online: boolean;
  /** When set, a restart was requested and is pending until the node observes it on its next heartbeat. */
  restartRequestedUtc: string | null;
  /** The pool the node serves; empty or null is the default (untargeted) pool. */
  pool: string | null;
  /** How many runs the node executes at once, as it reported on its last poll. */
  runSlots: number;
  /** How many runs the node was executing at its last poll. */
  busyRuns: number;
}

/** The outcome of purging the fleet registry's offline nodes: how many dead entries were removed. */
export interface NodePurgeResult {
  removed: number;
}

/** One worker pool's desired compute state and its live resolution. `pool` is the empty string for the default
 *  (untargeted) pool. `replicaTarget` is the count the autoscaler holds, exactly what the control plane answers the
 *  scaler with: the greatest of the demand (`eligibleQueuedRuns` over `runSlotsPerNode`, rounded up, plus
 *  `busyNodes`), the always-on floor, and the manual override while active. */
export interface WorkerPool {
  pool: string;
  minReplicas: number;
  manualReplicas: number;
  manualUntilUtc: string | null;
  manualActive: boolean;
  queuedRuns: number;
  replicaTarget: number;
  /** How many of this pool's workers are online right now; when below replicaTarget, the pool is spinning one up. */
  onlineNodes: number;
  updatedUtc: string | null;
  updatedBy: string | null;
  /** The part of the backlog a node could take right now: queued runs no gate holds back. */
  eligibleQueuedRuns: number;
  /** Online workers of the pool that hold at least one run. */
  busyNodes: number;
  /** What one worker of the pool executes at once, as the workers report it. */
  runSlotsPerNode: number;
}

// ---- Dispatch ---------------------------------------------------------------------------------------------------------

/** Per-pool counts as the dispatcher sees them: the backlog, what is executing, and what capacity is online. */
export interface DispatchPoolView {
  pool: string;
  queuedRuns: number;
  leasedRuns: number;
  queuedTasks: number;
  leasedTasks: number;
  onlineNodes: number;
  freeRunSlots: number;
}

/** Why a queued run is not being handed out right now; the empty string means it is eligible. */
export type DispatchBlockReason = "" | "pipeline-busy" | "wave-gated" | "group-cap" | "no-eligible-node";

/** One queued run and the gate holding it back, if any. */
export interface QueuedRunView {
  runId: string;
  pipelineId: string;
  pool: string;
  groupId: string | null;
  groupWave: number;
  groupMaxConcurrency: number | null;
  enqueuedUtc: string;
  attempt: number;
  cancelRequested: boolean;
  blocked: DispatchBlockReason;
}

/** One run handed to a node: who holds it, under which attempt, and until when unless renewed. `state` is `leased`,
 *  `reserved` (a hand-out whose journal write is in flight) or `expiring` (the lease lapsed and its disposition is
 *  in flight). */
export interface LeasedRunView {
  runId: string;
  pipelineId: string;
  pool: string;
  groupId: string | null;
  node: string | null;
  attempt: number;
  leasedUtc: string | null;
  leaseExpiresUtc: string | null;
  cancelRequested: boolean;
  state: string;
}

export interface QueuedTaskView {
  taskId: string;
  pool: string;
  enqueuedUtc: string;
  blocked: DispatchBlockReason;
}

export interface LeasedTaskView {
  taskId: string;
  pool: string;
  node: string | null;
  leasedUtc: string | null;
  leaseExpiresUtc: string | null;
  cancelRequested: boolean;
  state: string;
}

/** One node as the dispatcher's registry last heard from it. */
export interface DispatchNodeView {
  name: string;
  version: string | null;
  pools: string[];
  runSlots: number;
  freeRunSlots: number;
  taskSlots: number;
  freeTaskSlots: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
  startedUtc: string;
  restartRequestedUtc: string | null;
  online: boolean;
}

/** The dispatcher as it sees itself: ownership, the last housekeeping passes, and the whole queue with every gate
 *  and lease explained. A replica that does not own dispatch reports `active: false` with an empty queue. */
export interface DispatchSnapshot {
  active: boolean;
  owner: string | null;
  activatedUtc: string | null;
  lastReconcileUtc: string | null;
  lastTickUtc: string | null;
  /** Node polls currently parked waiting for work or a signal. */
  waiters: number;
  pools: DispatchPoolView[];
  queuedRuns: QueuedRunView[];
  leasedRuns: LeasedRunView[];
  queuedTasks: QueuedTaskView[];
  leasedTasks: LeasedTaskView[];
  nodes: DispatchNodeView[];
}

/** A change to a pool's desired state. Every field is optional: send `minReplicas` to set/clear the always-on
 *  floor, or `manualReplicas` (with `manualForMinutes`) to bring workers up for a bounded window. */
export interface WorkerPoolScaleRequest {
  pool: string | null;
  minReplicas?: number;
  manualReplicas?: number;
  manualForMinutes?: number;
}

// ---- Repo sources -----------------------------------------------------------------------------------------------------

export interface RepoSource {
  id: string;
  name: string;
  remoteUrl: string;
  branch: string;
  enabled: boolean;
  syncIntervalSeconds: number;
  nextSyncUtc: string | null;
  lastSyncUtc: string | null;
  lastSyncedSha: string | null;
  lastError: string | null;
  /** A secret reference (${keyvault:...}/${env:...}) for the git token, never the token itself; null uses the host env. */
  credentialReference: string | null;
  /** The git username paired with the token (for Bitbucket); null uses the token-only placeholder. */
  credentialUsername: string | null;
  /** The repo-relative flow files this source does NOT import (the preview-first selection); empty imports all. */
  excludedFlowPaths: string[];
  createdUtc: string;
  updatedUtc: string;
}

export interface RegisterRepoSourceRequest {
  name: string;
  remoteUrl: string;
  branch?: string | null;
  syncIntervalSeconds?: number | null;
  enabled?: boolean | null;
  credentialReference?: string | null;
  credentialUsername?: string | null;
  /** The flow files to leave out of the import; null or omitted imports every *.flow.yaml. */
  excludedFlowPaths?: string[] | null;
}

/** The body to preview a repo's flows without importing them (a read-only discover). */
export interface DiscoverRepoRequest {
  remoteUrl: string;
  branch?: string | null;
  credentialReference?: string | null;
  credentialUsername?: string | null;
}

/** One *.flow.yaml a discover found, for the selection wizard. */
export interface DiscoveredFlow {
  relativePath: string;
  flowName: string | null;
  kind: string | null;
  sizeBytes: number;
  parseOk: boolean;
  parseError: string | null;
  /** Secret-redacted content for preview; null when the file is too large to inline. */
  content: string | null;
}

export interface RepoSourceRegistered {
  id: string;
}

// ---- Source discovery (JSON/XML flatten formula) ----------------------------------------------------------------------

/** The body to discover a flattenable JSON/XML source and generate its ingestion YAML. */
export interface SourceDiscoverRequest {
  /** A file, folder, or URI the deployment can reach (e.g. abfss://.../data.json or a folder of files). */
  location: string;
  /** Pin the format (json/ndjson/jsonl/xml); omitted infers it from the extension or the folder pattern. */
  format?: string | null;
  /** Glob for a folder location, e.g. "*.json" or "*.xml"; ignored for a single file. */
  pattern?: string | null;
  /** Recurse into sub-folders when the location is a folder. */
  recursive?: boolean | null;
  /** Override the record grain (JSON rootPath / XML rowXPath); omitted auto-detects it from the sample. */
  rootPath?: string | null;
  /** Files to scan (folder locations), default 100. */
  maxFiles?: number | null;
  /** Records to scan, 0 = all, default 0. */
  maxRecords?: number | null;
  /** Max nesting depth to inspect, default 10. */
  maxDepth?: number | null;
  /** schema.defaultColumnType for the generated flow; omitted uses SQLFlow's varchar(255) default. */
  defaultColumnType?: string | null;
  /** Stream the scan's phases into the activity trace under this subject (the GUI's bottom panel tails it); the
   * location is used as the subject so repeat scans of the same source keep a bounded scrollback. Omitted runs headless. */
  traceSubject?: string | null;
}

/** One discovered path: its address, what it points at, the column it becomes, and its per-record presence. */
export interface DiscoveredPath {
  path: string;
  /** "value" | "container" | "repeating". */
  kind: string;
  column: string;
  recordCount: number;
  /** False when the path is missing from some scanned records (schema drift). */
  present: boolean;
}

/** One discovered output column: name, target SQL type, nullability, and (flatten mode) its source path. */
export interface DiscoveredColumn {
  name: string;
  sqlType: string;
  nullable: boolean;
  /** The originating JSON/XML path for a flattened column; null for a tabular column. */
  sourcePath: string | null;
}

/** A key/value pair for the generated source.options block. */
export interface SourceOption {
  key: string;
  value: string | null;
}

/** The discovery outcome. mode is "flatten" (JSON/XML: paths + grain) or "columnar" (CSV/Excel/Parquet: columns). */
export interface SourceDiscoverResult {
  /** "flatten" | "columnar". */
  mode: string;
  sourceType: string;
  /** How the format was decided: "explicit" | "extension" | "high" | "medium" | "low". */
  detectionConfidence: string;
  /** Human-readable reasons for the detected format. */
  detectionEvidence: string[];
  /** The auto-detected record grain (rootPath/rowXPath), or null when pinned or none was found. */
  autoDetectedGrain: string | null;
  filesScanned: number;
  recordsScanned: number;
  /** True when at least one path is missing from some records. */
  schemaDrift: boolean;
  /** Nested-source path structure (flatten mode); empty for columnar sources. */
  paths: DiscoveredPath[];
  /** Output columns (columnar mode); empty for flatten mode, whose columns come from the paths. */
  columns: DiscoveredColumn[];
  options: SourceOption[];
  generatedYaml: string;
}

// ---- Lineage -----------------------------------------------------------------------------------------------------------

/** One (server, database, schema) grouping in the catalog with how many objects it holds. A null
 * database/schema is an object whose identity was only partially resolved (an offline sync). */
export interface LineageSchema {
  serverRef: string;
  database: string | null;
  schema: string | null;
  objectCount: number;
}

/** One (server, database, schema, kind) grouping with its object count: the per-kind breakdown the catalog
 * tree renders as Tables/Views/Procedures folders under each schema. */
export interface SchemaKindCount {
  serverRef: string;
  database: string | null;
  schema: string | null;
  kind: string;
  objectCount: number;
}

/** One file endpoint decomposed to its canonical parent for the source tree: the origin system (storage
 * account / SFTP host:port / local filesystem), the container (Azure container or UNC share; null otherwise),
 * the folder path (slash-joined; null at the root), and the leaf name. `key` is the object key, so a leaf
 * opens its dossier. The file twin of a schema-kind row: a file groups under its storage account exactly as a
 * table groups under its database. */
export type FileOriginKind =
  | "AzureStorage" | "AmazonS3" | "GoogleCloud" | "Sftp" | "NetworkShare" | "Local" | "Other";

/** One database object a flow lands data into (a written/created target), for a file source's provenance. */
export interface LandingObject {
  key: string;
  database: string | null;
  schema: string | null;
  name: string;
  kind: string;
}

/** One pipeline that reads a file source, with where it lands the data. */
export interface FileConsumer {
  pipelineId: string;
  flow: string;
  kind: string;
  repoId: string;
  lands: LandingObject[];
}

/** One pipeline that produces a file (a copy/acquire/export): where the file comes from. */
export interface FileProducer {
  pipelineId: string;
  flow: string;
  kind: string;
  repoId: string;
}

/** A file source's provenance: which pipelines produce it and which consume it (with landing tables). */
export interface FileFlows {
  producers: FileProducer[];
  consumers: FileConsumer[];
}

export interface FileNode {
  key: string;
  originKind: FileOriginKind;
  origin: string;
  container: string | null;
  path: string | null;
  name: string;
}

export interface LineageObject {
  key: string;
  serverRef: string;
  database: string | null;
  schema: string | null;
  name: string;
  kind: string;
  /** The object's depth in the estate-wide data-movement graph (0 = a source nothing produces); null when the
   * object takes part in no data movement. */
  level: number | null;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface LineageObjectDetail extends LineageObject {
  /** The module body (view/procedure/function); null for plain tables, an unconnected sync, or an encrypted module. */
  definition: string | null;
  /** The generating script (CREATE ...); null when no lineage tier saw the object created. */
  script: string | null;
  scriptTier: string | null;
  scriptUpdatedUtc: string | null;
  /** The interpreted primary/business key (comma-joined, in key order), read from the codebase (a PRIMARY KEY
   * clause, the loading flow's YAML key columns, or the MERGE match key); null when nothing names a key. */
  keyColumns: string | null;
  /** How the key was interpreted: "Constraint" | "Declared" | "Merge"; null with no key. */
  keyOrigin: string | null;
}

/** One interpreted data-model relationship as seen from a dossier's object: how this table joins the other.
 * ownColumns/otherColumns pair positionally; origin "Constraint" is an explicit FOREIGN KEY clause in the
 * codebase, "Join" an inference from the equality predicates the code actually joins on; occurrences counts
 * the distinct scripts exhibiting it (the canonical join path scores highest). */
export interface ObjectRelationship {
  name: string | null;
  origin: "Constraint" | "Join" | string;
  tier: string;
  occurrences: number;
  otherObjectKey: string;
  otherDatabase: string | null;
  otherSchema: string | null;
  otherName: string;
  ownColumns: string;
  otherColumns: string;
}

export interface LineageObjectColumn {
  ordinal: number;
  name: string;
  dataType: string | null;
  nullable: boolean;
  /** Where the column was learned: read live (Derived) or parsed from the CREATE a run executed (Observed/Declared). */
  tier: string;
}

/** The script behind any lineage node in one shape: a pipeline's YAML, a view/procedure's module body, or a
 * table's generated CREATE TABLE. `language` is "yaml" or "sql"; `source` is Authored / Module / a script tier. */
export interface NodeScript {
  key: string;
  kind: string;
  language: string;
  script: string | null;
  source: string | null;
  name: string | null;
}

export interface LineageEdge {
  id: number;
  repoId: string;
  pipelineId: string | null;
  flow: string | null;
  viaModule: string | null;
  relation: string;
  objectKey: string;
  objectName: string;
  /** The object's database and schema from the global registry (proper-cased); null for a file or a
   * partially-resolved identity. */
  objectDatabase: string | null;
  objectSchema: string | null;
  tier: string;
  /** The object's catalog kind (Table, View, Procedure, ...) from the global registry; null when the registry
   * does not know the key. This is what classifies a DB-managed view (no writing pipeline) as a view. */
  objectKind?: string | null;
}

/** Everything known about one object in a single payload: identity and metadata, columns (with the
 * interpreted key), the lineage edges that reference it, and the interpreted data-model relationships in
 * both directions. The "ask about this object" aggregate behind the catalog tree's details panel. */
export interface ObjectDossier {
  object: LineageObjectDetail;
  columns: LineageObjectColumn[];
  edges: LineageEdge[];
  /** Tables this object references (it holds the joining/foreign-key side). */
  references: ObjectRelationship[];
  /** Tables that reference this object (it is the referenced/key side). */
  referencedBy: ObjectRelationship[];
  /** The data subscribers that consume this object: who breaks if it changes. */
  subscribers: ObjectSubscriber[];
}

/** One data subscriber consuming an object, resolved from its read edges. `queries` names the subscriber's
 * own queries that reference this object, so the link is evidence rather than assertion. */
export interface ObjectSubscriber {
  /** The subscriber's node key; opens its dossier. */
  key: string;
  name: string;
  /** What consumes the data: PowerBI, Tableau, Excel, Notebook, Application, ... */
  type: string;
  owner: string | null;
  description: string | null;
  /** Remarks about the report's state (stale, superseded, unopenable), not what it is for. */
  notes: string | null;
  url: string | null;
  queries: string[];
}

/** One data subscriber in the estate-wide list: what consumes the warehouse and how much of it it reads. */
export interface Subscriber {
  key: string;
  name: string;
  type: string;
  owner: string | null;
  description: string | null;
  /** Remarks about the report's state (stale, superseded, unopenable), not what it is for. */
  notes: string | null;
  url: string | null;
  repoId: string;
  /** The repo-relative subscribers.yaml that declares it. */
  file: string;
  queryCount: number;
  /** How many distinct warehouse objects its queries read, from the lineage edges. */
  objectCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

/** Everything one subscriber consumes: the consumption-side twin of the object dossier. */
export interface SubscriberDossier {
  subscriber: Subscriber;
  queries: SubscriberQuery[];
  objects: SubscriberObject[];
}

/** One query a subscriber runs, and the object keys parsing it proved it reads. */
export interface SubscriberQuery {
  ordinal: number;
  name: string;
  serverRef: string;
  sql: string;
  objectKeys: string[];
}

/** One warehouse object a subscriber reads, with the subscriber's queries that reference it. */
export interface SubscriberObject {
  key: string;
  database: string | null;
  schema: string | null;
  name: string;
  kind: string;
  /** The object's depth in the estate-wide data-movement graph; null when it takes part in none. */
  level: number | null;
  queries: string[];
}

export interface WavePipeline {
  id: string;
  name: string;
  kind: string;
}

export interface Wave {
  wave: number;
  pipelines: WavePipeline[];
}

/** One selectable project for the lineage graph's scope picker: a repo-root folder, the repo it lives in (so the
 * picker can show "repo / project" and disambiguate a folder name shared across repos), and its active-flow count. */
export interface LineageProject {
  repoId: string;
  repoName: string;
  project: string;
  flowCount: number;
}

/** One pipeline node in a project's cross-repo lineage closure. `isSeed` marks a flow in the selected project
 * itself (its base objects seed the graph); a non-seed flow was reached downstream, possibly in another repo, which
 * is why `repoId`/`repoName` travel with every node. `depth` is how many downstream hops from the seed it sits at. */
export interface ProjectGraphPipeline {
  id: string;
  name: string;
  kind: string;
  wave: number;
  repoId: string;
  repoName: string;
  relativePath: string;
  isSeed: boolean;
  depth: number;
  /** False when the flow depends on a module (an executed procedure, a read view) whose body was never
   * harvested, so its true reads/writes are unknown; `incompleteReason` says which module and why. The graph
   * must render this as "not derived yet", never as an edgeless fact. */
  lineageComplete?: boolean;
  incompleteReason?: string | null;
}

/** One object node of the drawable project graph: key (the node id), display name, resolved kind
 * (table/view/file), where it lives (database.schema, null for a file), and whether it sits on the depth-capped
 * frontier with un-included consumers. */
export interface ProjectGraphObject {
  key: string;
  name: string;
  kind: string;
  location: string | null;
  frontier: boolean;
}

/** One resolved, drawable edge of the project graph. `source`/`target` are node ids (a pipeline id or an object
 * key). `label` is what the arrow says (writes/creates/reads/view in the flows view; the flow name in the
 * objects view). `pipelineId` colors the edge per flow; null for a DB-managed view's derivation edge. */
export interface ProjectGraphDrawEdge {
  source: string;
  target: string;
  label: string;
  pipelineId: string | null;
}

/** A project's lineage as one cross-repo subgraph. `pipelines`/`edges` are the underlying facts; `objects`,
 * `flowGraph`, and `objectGraph` are the DRAWABLE graph derived server-side (nodes typed from the registry,
 * view bodies wired to base tables, procedures excluded). The client renders this verbatim: it lays out and
 * paints, and never re-derives semantics from the facts. */
export interface ProjectGraph {
  pipelines: ProjectGraphPipeline[];
  edges: LineageEdge[];
  frontier: string[];
  truncated: boolean;
  objects: ProjectGraphObject[];
  flowGraph: ProjectGraphDrawEdge[];
  objectGraph: ProjectGraphDrawEdge[];
}

/** One repo whose lineage references an object: how many of its edges touch the object and whether a flow there
 * writes/creates it. Ranked writing-repo-first so a search jump to the lineage graph opens on the repo that shows
 * how the object is populated. */
export interface ObjectRepo {
  repoId: string;
  repoName: string;
  edgeCount: number;
  writes: boolean;
}

/** A file-ingestion pipeline whose source spec matches a file: the flow that would ingest it, resolved from the
 * flow definitions in the catalog (no run required). `pathConfirmed` is true when the full path (not just the file
 * name) was matched, so a definitively-located match ranks above a name-only one. */
export interface FilePipelineMatch {
  pipelineId: string;
  pipelineName: string;
  repoId: string;
  repoName: string;
  sourceType: string;
  sourceLocation: string | null;
  pattern: string;
  pathConfirmed: boolean;
}

export interface FlowDependency {
  id: number;
  repoId: string;
  fromFlow: string;
  toFlow: string;
  fromPipelineId: string;
  toPipelineId: string;
  viaObjects: string;
}

// ---- Search -------------------------------------------------------------------------------------------------------------

export interface ObjectHit {
  key: string;
  name: string;
  kind: string;
  serverRef: string;
  database: string | null;
  schema: string | null;
}

export interface ColumnHit {
  objectKey: string;
  objectName: string;
  columnName: string;
  dataType: string | null;
  nullable: boolean;
}

export interface DefinitionHit {
  key: string;
  name: string;
  kind: string;
  snippet: string;
  // Which body carried the match: "Module" (the live sys.sql_modules definition) or "Script" (the emitted DDL).
  source: string;
}

export interface FileHit {
  name: string;
  path: string | null;
  runId: string;
  flowName: string;
  flowKind: string;
  rows: number;
  columns: number;
  sizeBytes: number;
  runUtc: string | null;
  // The flow that ingested the file and its repo, so the hit can jump to that flow's node in the lineage graph.
  pipelineId: string;
  repoId: string | null;
  repoName: string | null;
}

export interface FlowHit {
  id: string;
  name: string;
  kind: string;
  batch: string | null;
  relativePath: string;
  repoId: string;
  repoName: string;
  // Where the term matched: "Name", "Path", or "Body" (inside the YAML).
  matchedIn: string;
  snippet: string;
}

// A column a FLOW produces: its output name, the raw source column behind it, or the expression computing it.
// Unlike ColumnHit this needs no warehouse schema sync, so it sees columns that exist only inside a pipeline.
export interface FlowColumnHit {
  pipelineId: string;
  flowName: string;
  flowKind: string;
  batch: string | null;
  repoId: string;
  repoName: string;
  // "declared" (authored in the YAML) or "detected" (inferred by a run from the raw data).
  kind: string;
  ordinal: number;
  columnName: string;
  sourceColumn: string | null;
  dataType: string | null;
  expression: string | null;
  // Where the term matched: "Column", "Source", or "Expression".
  matchedIn: string;
}

// SQL a flow actually executed, collapsed to one row per (flow, step) with an occurrence count.
export interface StatementHit {
  pipelineId: string;
  flowName: string;
  flowKind: string;
  step: string;
  occurrences: number;
  runId: string;
  lastSeenUtc: string | null;
  snippet: string;
}

// One data subscriber matching a search: a report, workbook, or application that CONSUMES the warehouse. `key`
// opens its dossier. `notes` is carried because it is often why the row matched (searching "Incomplete dataset"
// finds every consumer whose lineage is only partial).
export interface SubscriberHit {
  key: string;
  name: string;
  /** The consuming tool: PowerBI, Tableau, Excel, ... */
  type: string;
  owner: string | null;
  description: string | null;
  notes: string | null;
  /** Where the report lives: a Power BI URL, a workbook path, a share, a repo. Free text; only http(s) is clickable. */
  url: string | null;
  repoId: string;
  /** The repo-relative subscribers.yaml that declares it. */
  file: string;
}

// One category of a combined search: the full match count plus a short preview of the top hits.
export interface SearchCategory<T> {
  total: number;
  items: T[];
}

// The combined result of a single global search across every catalog surface. `tokens` is how the raw query was
// parsed (a multi-word query matches word by word, not as a literal phrase); `statementWindowDays` is how far back
// the executed-SQL surface reached.
export interface AllSearchResult {
  query: string;
  tokens: string[];
  statementWindowDays: number;
  objects: SearchCategory<ObjectHit>;
  columns: SearchCategory<ColumnHit>;
  definitions: SearchCategory<DefinitionHit>;
  files: SearchCategory<FileHit>;
  flows: SearchCategory<FlowHit>;
  flowColumns: SearchCategory<FlowColumnHit>;
  statements: SearchCategory<StatementHit>;
  subscribers: SearchCategory<SubscriberHit>;
}

// ---- Users ---------------------------------------------------------------------------------------------------------------

export interface User {
  id: string;
  username: string;
  email: string | null;
  displayName: string | null;
  role: string;
  provider: string;
  active: boolean;
  createdUtc: string;
  updatedUtc: string;
  lastLoginUtc: string | null;
}

export interface Role {
  name: string;
  scopes: string;
  description: string;
}

/** Renames a user and rewrites their display name and email; null clears an optional field. */
export interface UpdateUserProfileRequest {
  username: string;
  email: string | null;
  displayName: string | null;
}

export interface CreateUserRequest {
  username: string;
  password: string;
  role: string;
  email?: string | null;
  displayName?: string | null;
}

// ---- Personal access tokens -------------------------------------------------------------------------------------

export interface AccessToken {
  id: string;
  name: string;
  /** The recognizable, non-secret lead of the token (for example "sqlf_a1b2c3"). */
  prefix: string;
  scopes: string[];
  createdUtc: string;
  /** Null when the token never expires; a past value means it is expired. */
  expiresUtc: string | null;
  lastUsedUtc: string | null;
  /** Non-null once the token is revoked. */
  revokedUtc: string | null;
}

export interface CreateAccessTokenRequest {
  name: string;
  /** Capped server-side to the caller's own scopes; omit/empty defaults to all of them. */
  scopes?: string[];
  /** Null (omitted) means the token never expires. */
  expiresInDays?: number | null;
}

/** The create response: the listing view plus the one-time secret, shown exactly once. */
export interface CreatedAccessToken {
  token: AccessToken;
  secret: string;
}

// ---- Datasources and ad-hoc compute -------------------------------------------------------------------------------

/** One datasource the estate declares: a connection reference plus how the catalog sees it used. */
export interface Datasource {
  /** The connection reference: a whole ${...} / @alias token, or the hashed identity of an inline literal. */
  reference: string;
  /** Best-effort provider kind read from the flow definitions (MSSQL / AZDB / MySQL / PostgreSQL / Oracle). */
  kind: string | null;
  /** True when a worker can resolve the reference for ad-hoc compute (only whole references can travel). */
  resolvable: boolean;
  sourcePipelines: number;
  targetPipelines: number;
}

export type ComputeOperation =
  | "testConnection"
  | "listDatabases"
  | "listSchemas"
  | "listObjects"
  | "searchObjects"
  | "introspectObject"
  | "detectUniqueKey"
  | "missingIndexes"
  | "statisticsHealth"
  | "indexUsage"
  | "topQueries";

/** The body that requests an ad-hoc compute task. References only; a secret is never sent. */
export interface ComputeTaskRequest {
  reference: string;
  operation: ComputeOperation;
  /** Provider kind for a ${...} reference (default SQL Server); an @alias resolves its own kind. */
  kind?: string | null;
  /** Routes the task to a node serving this pool (a node that can reach the source); omit for any node. */
  pool?: string | null;
  database?: string | null;
  schema?: string | null;
  objectName?: string | null;
  nameLike?: string | null;
  searchTerm?: string | null;
  includeTables?: boolean;
  includeViews?: boolean;
  includeSystem?: boolean;
  offset?: number;
  limit?: number;
  /** detectUniqueKey: null auto-samples large tables, 0 forces a full scan, positive sets an explicit sample. */
  sampleSize?: number | null;
  maxKeyColumns?: number;
  maxCandidates?: number;
  verifyCandidates?: boolean;
  trustDeclaredKeys?: boolean;
}

export interface ComputeTaskAccepted {
  taskId: string;
  status: string;
}

/** A compute task as the task list shows it (no result body). */
export interface ComputeTaskSummary {
  taskId: string;
  operation: ComputeOperation;
  sourceRef: string;
  providerKind: string | null;
  pool: string | null;
  status: string;
  requestedBy: string | null;
  enqueuedUtc: string;
  startUtc: string | null;
  endUtc: string | null;
  claimedByNode: string | null;
  cancelRequestedUtc: string | null;
  error: string | null;
  hasResult: boolean;
  /** The [db.]schema.name an object-scoped task ran against (introspect / detect key); null otherwise. */
  target: string | null;
}

/** A single compute task; result is the operation's JSON document once the task succeeded. */
export interface ComputeTask extends Omit<ComputeTaskSummary, "hasResult"> {
  result: unknown | null;
}

// ---- Compute result payloads (the shapes of ComputeTask.result per operation) --------------------------------------

export interface ConnectionTestResult {
  ok: boolean;
  kind: string;
  serverVersion: string | null;
  database: string | null;
  elapsedMs: number;
}

export interface DatasourceDatabase {
  name: string;
  collation: string | null;
  state: string | null;
}

export interface DatasourceSchema {
  name: string;
  owner: string | null;
}

export interface DatasourceObject {
  schema: string;
  name: string;
  type: "Table" | "View";
  approxRows: number;
}

export interface DatasourceObjectPage {
  items: DatasourceObject[];
  offset: number;
  limit: number;
  total: number;
  hasMore: boolean;
}

export interface DatasourceObjectMatch {
  schema: string;
  name: string;
  type: "Table" | "View";
  rank: number;
}

export interface IntrospectedColumn {
  name: string;
  ordinal: number;
  nativeType: string;
  isNullable: boolean;
  collation: string | null;
  isIdentity: boolean;
  computedExpression: string | null;
  defaultExpression: string | null;
  isPrimaryKeyMember: boolean;
}

export interface IntrospectedIndex {
  name: string;
  isPrimaryKey: boolean;
  isUnique: boolean;
  isClustered: boolean;
  isColumnStore: boolean;
  keyColumns: string[];
}

/** Why a view's SQL is or is not present. "PermissionDenied" is the common one: a reporting login may read a
 *  view's rows without holding the privilege to read its source (VIEW DEFINITION on SQL Server, SHOW VIEW on
 *  MySQL), and that calls for asking someone for a grant rather than concluding the view has no body. */
export type DefinitionAvailability = "NotApplicable" | "Available" | "PermissionDenied" | "Unavailable";

export interface IntrospectedObject {
  name: { database: string | null; schema: string; name: string; schemaQualified: string; qualifiedName: string };
  type: "Table" | "View";
  columns: IntrospectedColumn[];
  indexes: IntrospectedIndex[];
  /** A view's own SQL. Null for a table, and for a view whose source could not be read; always read
   *  definitionAvailability to know which. */
  definition: string | null;
  definitionAvailability: DefinitionAvailability;
  isTemporal: boolean;
}

export interface IntrospectionResult {
  found: boolean;
  object?: IntrospectedObject;
}

export interface UniqueKeyCandidate {
  columns: string[];
  isUnique: boolean;
  verified: boolean;
  declared: boolean;
  distinct: number;
  nulls: number;
  rows: number;
  duplicates: number;
  estimated: boolean;
  selectivity: number;
}

export interface UniqueKeyReport {
  objectName: string | null;
  totalRows: number;
  scannedRows: number;
  sampled: boolean;
  columns: { column: string; distinct: number; nulls: number; scanned: number; selectivity: number }[];
  candidates: UniqueKeyCandidate[];
  excludedColumns: { column: string; reason: string }[];
  note: string | null;
}

// ---- Notifications (self-service: the caller's own subscriptions) ------------------------------------------------

/** Whether one delivery channel is usable on this deployment, and which configured provider backs it. */
export interface NotificationChannelAvailability {
  available: boolean;
  /** The configured provider name (for example "smtp" or "slack"); null when the channel is not configured. */
  provider: string | null;
}

/** The deployment's notification capabilities plus the defaults a new subscription starts from. */
export interface MyNotificationOptions {
  enabled: boolean;
  email: NotificationChannelAvailability;
  slack: NotificationChannelAvailability;
  /** Every event kind a subscription can select (for example "run_failed"). */
  kinds: string[];
  /** The kinds preselected for a new subscription. */
  defaultKinds: string[];
  modes: string[];
  /** The account email used when a subscription carries no explicit address; null when the account has none. */
  userEmail: string | null;
  defaultDigestIntervalMinutes: number;
  defaultCooldownMinutes: number;
  /** How this deployment produces estate digests, and the bounds an on-demand window must fall inside. */
  estateDigest: EstateDigestOptions;
}

/** The deployment's estate-digest cadence: what the periodic generator does, and what a manual window may ask for. */
export interface EstateDigestOptions {
  /** Whether the control plane generates a digest on its own schedule. */
  enabled: boolean;
  /** The period between scheduled digests, in minutes. */
  intervalMinutes: number;
  /** The shortest and longest period an on-demand digest may cover, in minutes. */
  minWindowMinutes: number;
  maxWindowMinutes: number;
}

export interface NotificationSubscription {
  id: string;
  channel: "email" | "slack";
  mode: "immediate" | "digest";
  kinds: string[];
  /** Wildcard filter over flow names; null means the subscription covers all flows. */
  flowPattern: string | null;
  /** Null means messages go to the account email. */
  emailAddress: string | null;
  /** Null means the user is direct-messaged. */
  slackTarget: string | null;
  digestIntervalMinutes: number;
  cooldownMinutes: number;
  enabled: boolean;
  lastSentUtc: string | null;
  /** When the next digest is due; null for immediate subscriptions or when nothing is pending. */
  nextDueUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreateNotificationSubscriptionRequest {
  channel: "email" | "slack";
  mode?: "immediate" | "digest";
  /** Omit to start from the server's default kinds. */
  kinds?: string[];
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number;
  cooldownMinutes?: number;
}

/** Partial update: null/omitted keeps the current value; an empty string clears flowPattern/emailAddress/slackTarget. */
export interface UpdateNotificationSubscriptionRequest {
  mode?: "immediate" | "digest";
  kinds?: string[];
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number;
  cooldownMinutes?: number;
  enabled?: boolean;
}

/** One outbound message, tracked from queueing through delivery. */
export interface NotificationDelivery {
  id: string;
  subscriptionId: string;
  channel: "email" | "slack";
  target: string;
  subject: string;
  status: "queued" | "sending" | "sent" | "failed";
  attempts: number;
  /** How many notification events the message carries (a digest bundles several). */
  eventCount: number;
  lastError: string | null;
  createdUtc: string;
  sentUtc: string | null;
}

/** An accepted send (a test message, a digest): the delivery to watch for in the recent-deliveries list. */
export interface NotificationQueuedDelivery {
  deliveryId: string;
}

/**
 * One estate digest as a list shows it: a composed summary of every notification event in a window. The control
 * plane generates one per configured period whether or not anybody subscribes, and a person can generate one on
 * demand, so "what failed last night" is answerable even on a deployment with no channel configured.
 */
export interface NotificationDigestSummary {
  id: string;
  /** "scheduled" for the periodic generator, "manual" for one a person asked for. */
  origin: "scheduled" | "manual";
  periodStartUtc: string;
  periodEndUtc: string;
  generatedUtc: string;
  /** Who asked for a manual digest; null for a scheduled one (and for a deleted account). */
  generatedBy: string | null;
  subject: string;
  eventCount: number;
  flowCount: number;
  failedCount: number;
  cancelledCount: number;
  skippedCount: number;
  assertionFailedCount: number;
  /** The window held more events than one digest renders; the rest are carried into the next scheduled digest. */
  truncated: boolean;
}

/**
 * One flow's slice of a digest: what happened to it in the window, how often, and the run worth opening. `kind`
 * is the notification event kind ("run_failed", "assertion_failed", ...), not a run status.
 */
export interface NotificationDigestFlow {
  flowName: string;
  flowKind: string;
  kind: string;
  count: number;
  lastOccurredUtc: string;
  lastRunId: string;
  /** The flow behind the name, for the link to its page; a soft link, like the run id. */
  pipelineId: string;
  lastError: string | null;
}

/** One digest opened for reading: the summary, the per-flow rows behind it, and the composed bodies. */
export interface NotificationDigest {
  summary: NotificationDigestSummary;
  /** The per-flow table, most alarming first; empty when the window was quiet. */
  flows: NotificationDigestFlow[];
  /** The message exactly as it would be delivered (an email's text alternative, Slack's fallback). */
  textBody: string;
  htmlBody: string;
}

/**
 * Generates a digest over one period, most often a single day. Both instants are UTC ISO strings and both are
 * required; an end past the present is clamped to now, so asking for today yields today so far.
 */
export interface GenerateNotificationDigestRequest {
  fromUtc: string;
  toUtc: string;
}

/** Sends an already-generated digest through one of the caller's own subscriptions. */
export interface SendNotificationDigestRequest {
  subscriptionId: string;
}

// ---- Maintenance (run-trace storage retention) ------------------------------------------------------------------

/** How much per-run trace is stored, split by kind, and how many SQL-statement rows a cleanup would reclaim. SQL
 * statements are the generated SQL (heavy, near-identical every run, so purgeable); events are the run's execution
 * log (rows affected + timing), kept for history/analytics and never purged. "Prunable" counts statements of older,
 * superseded successful runs (each pipeline's latest run and every failed run are kept, as is anything newer than
 * the retention window). */
export interface RunTraceStorage {
  totalStatements: number;
  totalEvents: number;
  prunableStatements: number;
  /** How many runs' statements would be removed. */
  prunableRuns: number;
  /** The SQL-statement retention window (days); null means keep forever (age-based pruning off). */
  retentionDays: number | null;
}

/** A change to the SQL-statement retention: days to keep, or null to keep forever (age-based pruning off). */
export interface RunTraceRetentionUpdate {
  retentionDays: number | null;
}

/** The stored SQL-statement retention after an update; null means keep forever. */
export interface RunTraceRetention {
  retentionDays: number | null;
}

/** The outcome of a manual SQL-statement purge: how many statement rows were deleted. */
export interface RunStatementPurgeResult {
  statementsDeleted: number;
}

/** The outcome of a manual run-event purge: how many event rows were deleted. */
export interface RunEventPurgeResult {
  eventsDeleted: number;
}

// ---- Insights ------------------------------------------------------------------------------------------------------------------

/** One flow's performance over the insights window. Duration averages cover succeeded runs only; totals and
 * failure stats cover every terminal run. Trend compares against the equally-sized previous window. */
export interface FlowInsight {
  pipelineId: string;
  flowName: string;
  flowKind: string;
  batch: string | null;
  active: boolean;
  runs: number;
  failures: number;
  failureRate: number;
  avgDurationSeconds: number | null;
  maxDurationSeconds: number | null;
  totalDurationSeconds: number;
  rowsLoaded: number;
  rowsPerSecond: number | null;
  lastRunUtc: string;
  lastStatus: string;
  lastError: string | null;
  prevAvgDurationSeconds: number | null;
  prevRowsLoaded: number | null;
  durationTrendPercent: number | null;
}

export interface FlowInsights {
  windowDays: number;
  fromUtc: string;
  asOfUtc: string;
  totalRuns: number;
  totalFailures: number;
  totalDurationSeconds: number;
  totalRowsLoaded: number;
  flows: FlowInsight[];
}

export type InsightSeverity = "critical" | "warning" | "info";

/** One advisory on the attention list, with the evidence numbers inline in the detail sentence. One item per
 * flow (extra findings fold into an "Also:" note); a collapsed item aggregates a batch whose flows tripped the
 * same rule together (pipelineId null, flowCount > 1, flow names in the detail). */
export interface AttentionItem {
  severity: InsightSeverity;
  category: string;
  pipelineId: string | null;
  flowName: string | null;
  batch: string | null;
  flowCount: number;
  title: string;
  detail: string;
}

/** The attention list, capped at the requested limit; the counts cover everything found. */
export interface Attention {
  windowDays: number;
  asOfUtc: string;
  totalItems: number;
  criticalCount: number;
  warningCount: number;
  infoCount: number;
  items: AttentionItem[];
}

/** One actionable recommendation, from run history ("runHistory") or the newest DMV probe ("warehouseDmv").
 * suggestedSql is a ready-to-review statement, never something to execute unreviewed; it travels only when the
 * query asked includeSql=true, and hasSuggestedSql says one exists either way. */
export interface Recommendation {
  severity: InsightSeverity;
  category: string;
  source: "runHistory" | "warehouseDmv";
  title: string;
  detail: string;
  suggestedSql: string | null;
  hasSuggestedSql: boolean;
  pipelineId: string | null;
  flowName: string | null;
  reference: string | null;
  database: string | null;
}

/** Which warehouse DMV probe feeds the recommendations, and how fresh it is. */
export interface WarehouseProbeStatus {
  operation: ComputeOperation;
  taskId: string;
  reference: string;
  database: string | null;
  completedUtc: string | null;
}

/** The recommendations briefing, capped at the requested limit; the counts cover everything found. */
export interface Recommendations {
  windowDays: number;
  asOfUtc: string;
  totalItems: number;
  criticalCount: number;
  warningCount: number;
  infoCount: number;
  items: Recommendation[];
  warehouseProbes: WarehouseProbeStatus[];
}

/** One engine step's cost across the window's runs of a flow, with a sample of the SQL it executed. */
export interface StepInsight {
  step: string;
  occurrences: number;
  avgElapsedMs: number;
  maxElapsedMs: number;
  totalElapsedMs: number;
  rowsProcessed: number;
  sampleSql: string | null;
}

export interface StepInsights {
  pipelineId: string;
  flowName: string;
  windowDays: number;
  fromUtc: string;
  sampleRunId: string | null;
  steps: StepInsight[];
}

// ---- DataStream anomaly detection -----------------------------------------------------------------------------------------------

/** The ensemble's verdict on a stream. "stalled" means data has stopped arriving; "degraded" means two or
 * more independent detectors agree something is wrong; "watch" is a single detector's lead; "healthy" is
 * every detector quiet; "insufficient-history" means there were too few loads to judge. */
export type StreamStatus = "stalled" | "degraded" | "watch" | "healthy" | "insufficient-history";

/** The six independent tests that vote. The first three are PRIMARY (they answer "is data arriving at all")
 * and are the only ones that can raise a finding on their own; the last three measure volume and corroborate.
 * Two agreeing is the confirmation bar: no single detector, at any strength, can raise a critical. */
export type StreamDetectorName =
  | "silence" | "nullDays" | "cadence"
  | "rateChange" | "levelShift" | "volumeOutlier";

/** Which way a deviation went. The three are not equally urgent: no data is an outage, below is a
 * degradation, above is information. */
export type StreamDirection = "none" | "below" | "above";

/** One detector's verdict. Detectors that stayed quiet are reported too, with what they measured, so a
 * healthy stream is auditable rather than merely asserted. */
export interface StreamSignal {
  detector: StreamDetectorName;
  fired: boolean;
  /** Confidence in [0, 1]: 0 at the firing boundary, 1 where the evidence is unambiguous. */
  score: number;
  direction: StreamDirection;
  /** Whether this detector may raise a finding alone. A non-primary detector that fires corroborates a
   * primary one; alone it produces a lead to check. */
  primary: boolean;
  detail: string;
}

/** What one table's traffic normally looks like, learned from its own history after reprocessing was
 * excluded. This is the reference every verdict is stated against. */
/** A recurring delivery on top of the ordinary rhythm: the vendor who ships a bigger refill every fortnight,
 * the month-end settlement file. periodDays is the cycle length, or 0 when it repeats on a position in the
 * calendar month (which `monthly` marks). */
export interface StreamCycle {
  periodDays: number;
  monthly: boolean;
  /** How many times the cycle was actually seen in the window. */
  occurrences: number;
  /** True when the cycle days are heavier than an ordinary day, false for a regular light day. */
  heavier: boolean;
  cycleRows: number;
  ordinaryRows: number;
  /** The share of the unexplained variation this cycle accounts for, in [0, 1]. */
  lift: number;
  lastOccurrenceUtc: string | null;
  /** The next day the cycle is due: the date to check when the question is "did the big one arrive". */
  nextExpectedUtc: string | null;
  description: string;
}

export interface StreamPattern {
  shape:
    | "daily" | "weekdays" | "weekly" | "several-days-a-week" | "fortnightly" | "monthly" | "periodic"
    | "sporadic";
  /** The weekdays it reliably loads on, Monday first. Empty for a periodic or sporadic stream. */
  loadDays: string[];
  /** The median load on a day it loads, from the TRIMMED volumes, so a backfill is not what "typical" means. */
  typicalRows: number;
  /** The middle half of its loads: the band an ordinary day falls in. */
  lowRows: number;
  highRows: number;
  /** How often it NORMALLY delivers on a day it loads on, in [0, 1]: the median week, not the mean day. */
  reliability: number;
  /** The recurring larger (or smaller) delivery on top of the rhythm, null for a stream that has none. */
  cycle: StreamCycle | null;
  description: string;
}

/** A stream's measured normal: how much it writes, how often, and where it is trending. cadenceSource says
 * whether expectedGapDays came from the stream's cron ("schedule") or from its own history ("observed"). */
export interface StreamProfile {
  pattern: StreamPattern;
  cadence: string;
  expectedGapDays: number;
  cadenceSource: "schedule" | "observed";
  maxObservedGapDays: number;
  lastLoadUtc: string | null;
  lastRunUtc: string | null;
  /** Null means the stream has never loaded (never in this window). */
  daysSinceLastLoad: number | null;
  daysSinceLastRun: number | null;
  runDays: number;
  loadedDays: number;
  runs: number;
  failures: number;
  totalRowsInserted: number;
  totalRowsUpdated: number;
  totalRowsDeleted: number;
  avgRowsInsertedPerRun: number;
  avgRowsUpdatedPerRun: number;
  avgRowsDeletedPerRun: number;
  avgRowsWrittenPerLoadedDay: number;
  medianRowsWrittenPerLoadedDay: number;
  trendRowsPerDay: number;
  /** Days in the window it was expected to load on: the denominator unexpectedNullDays is read against, so a
   * board can say "3 of 30" rather than a bare "3". Zero for a stream with no rhythm the history supports. */
  expectedDays: number;
  /** Days it was expected to load on and wrote nothing: the headline number of this surface. */
  unexpectedNullDays: number;
  /** Of those, the days the flow RAN and still wrote nothing (an upstream problem). */
  emptyRunDays: number;
  /** Of those, the days the flow did not run at all (a scheduling or worker problem). */
  noRunDays: number;
  /** How many empty days its own normal delivery rate predicts: the bar the count above is judged against. */
  predictedNullDays: number;
  /** Load days trimmed as suspected reprocessing before anything was fitted, and the fence they hit. */
  trimmedLoadDays: number;
  trimFence: number;
}

/** One analysed day: what arrived, what was expected, and how the point was judged. */
export interface StreamPoint {
  date: string;
  rowsWritten: number;
  rowsInserted: number;
  rowsUpdated: number;
  rowsDeleted: number;
  runs: number;
  failures: number;
  /** Backfill runs on the day, excluded from every number above and from the analysis. */
  excludedBackfillRuns: number;
  expected: number;
  severity: number;
  anomaly: boolean;
  reason: string | null;
  imputed: boolean;
  immature: boolean;
  /** How reliably the stream loads on this kind of day, learned from its own history, in [0, 1]. */
  expectedLoadRate: number;
  /** The day wrote nothing AND it is a kind of day this stream loads on. */
  unexpectedNull: boolean;
  /** The day's volume was trimmed as suspected reprocessing before fitting; rowsWritten is the true number. */
  trimmed: boolean;
}

/** Which side of the estate boundary a stream sits on. "source" carries a vendor's data inwards; "internal"
 * derives one of our tables from another. They are separate questions with separate owners. */
export type StreamScope = "source" | "internal";

/** Where in the pipeline a stream sits. The sharper form of the same question: a vendor delivery failing at
 * "integration" means nothing arrived from them, while the same delivery failing at "file-ingestion" or
 * "archive" means it arrived and we did not take it in. */
export type StreamStage = "integration" | "file-ingestion" | "archive" | "derived";

/** The stream's last two weeks, compact enough to ride on every board row: rows written and the expectation
 * per day (oldest first, aligned), the zero-based positions of the days the analysis flagged and of the
 * expected days that wrote nothing, and how many trailing entries are still arriving and were never judged.
 * fromUtc is the day the first entry describes, null when there is no analysed day at all. */
export interface StreamSparkline {
  fromUtc: string | null;
  rows: number[];
  expected: number[];
  flaggedDays: number[];
  missedDays: number[];
  immatureDays: number;
}

/** One data stream: a flow, the table it writes, and the verdict. series is null on the board and populated
 * on the single-stream endpoint; sparkline is always present and is what a board row draws. */
export interface DataStream {
  pipelineId: string;
  flowName: string;
  flowKind: string;
  batch: string | null;
  active: boolean;
  targetObject: string | null;
  /** The data source this stream belongs to, from the repository layout or schedule membership rather than
   * from the flow's name, so a misnamed flow still groups with its siblings. */
  source: string;
  scope: StreamScope;
  /** How the scope was decided: "lineage" (walked the upstream chain), "origin" (reads nothing we produce),
   * "schema" (the target schema is configured as one side), or "kind". */
  scopeReason: string;
  stage: StreamStage;
  scheduleName: string | null;
  cron: string | null;
  timezone: string | null;
  status: StreamStatus;
  category: string;
  severity: InsightSeverity;
  confidence: number;
  agreeingDetectors: number;
  summary: string;
  profile: StreamProfile;
  signals: StreamSignal[];
  sparkline: StreamSparkline;
  series: StreamPoint[] | null;
}

/** The board: every analysed stream ranked most urgent first, plus the state counts. The counts cover every
 * ANALYSED stream, so totalStreams versus analyzedStreams says whether the sweep was capped. */
export interface DataStreams {
  windowDays: number;
  fromUtc: string;
  asOfUtc: string;
  includeBackfills: boolean;
  scope: StreamScope | "all";
  /** How many streams exist on each side, whichever side was analysed, so both can be offered without a
   * second request. */
  sourceStreams: number;
  internalStreams: number;
  /** Streams left out because they join no enabled schedule: reported rather than silently dropped. */
  unscheduledStreams: number;
  includeUnscheduled: boolean;
  totalStreams: number;
  analyzedStreams: number;
  excludedBackfillRuns: number;
  stalledCount: number;
  degradedCount: number;
  watchCount: number;
  healthyCount: number;
  insufficientHistoryCount: number;
  streams: DataStream[];
}

// ---- Warehouse health probe results (compute-task result shapes) ---------------------------------------------------------------

/** One missing-index advisory from sys.dm_db_missing_index_*, with a ready-to-review CREATE INDEX. */
export interface MissingIndexAdvisory {
  database: string;
  schema: string;
  table: string;
  equalityColumns: string | null;
  inequalityColumns: string | null;
  includedColumns: string | null;
  userSeeks: number;
  userScans: number;
  lastUserSeek: string | null;
  avgTotalUserCost: number;
  avgUserImpactPercent: number;
  improvementMeasure: number;
  suggestedIndexSql: string;
}

export interface MissingIndexesResult {
  database: string | null;
  advisories: MissingIndexAdvisory[];
}

/** Statistics freshness for one statistics object (sys.dm_db_stats_properties). */
export interface StatisticsAdvisory {
  schema: string;
  table: string;
  statisticName: string;
  rows: number;
  rowsSampled: number;
  samplePercent: number;
  modificationCounter: number;
  modificationPercent: number;
  lastUpdated: string | null;
  isStale: boolean;
  suggestedUpdateSql: string;
}

export interface StatisticsHealthResult {
  database: string | null;
  statistics: StatisticsAdvisory[];
  staleCount: number;
}

/** Read/write usage for one index since the counters last reset (sys.dm_db_index_usage_stats). */
export interface IndexUsageEntry {
  schema: string;
  table: string;
  indexName: string;
  indexType: string;
  isUnique: boolean;
  isPrimaryKey: boolean;
  userSeeks: number;
  userScans: number;
  userLookups: number;
  reads: number;
  writes: number;
  lastRead: string | null;
  sizeKb: number;
  rowCount: number;
  isUnused: boolean;
}

export interface IndexUsageResult {
  database: string | null;
  indexes: IndexUsageEntry[];
  unusedCount: number;
}

/** One cached statement ranked by total elapsed time (sys.dm_exec_query_stats). */
export interface ExpensiveQuery {
  database: string | null;
  statementText: string;
  executionCount: number;
  totalElapsedMs: number;
  avgElapsedMs: number;
  maxElapsedMs: number;
  totalCpuMs: number;
  avgCpuMs: number;
  totalLogicalReads: number;
  avgLogicalReads: number;
  lastExecutionTime: string;
  cachedSince: string;
}

export interface TopQueriesResult {
  database: string | null;
  queries: ExpensiveQuery[];
}

// ---- Chat assistant (/api/v1/chat) ---------------------------------------------------------------------------------

/** What the chat feature can do under the current deployment, so the GUI shows only affordances that work. */
export interface ChatCapabilities {
  enabled: boolean;
  provider: string;
  images: boolean;
  transcription: boolean;
  maxImages: number;
  maxImageBytes: number;
}

export interface ChatConversation {
  id: string;
  title: string;
  createdUtc: string;
  updatedUtc: string;
}

/** What a chat-history purge removed. */
export interface ChatConversationsPurged {
  conversations: number;
  messages: number;
}

/** One tool call an answer made (in call order); status is "started" | "completed" | "failed". */
export interface ChatToolCall {
  name: string;
  status: string;
}

export interface ChatMessage {
  id: number;
  ordinal: number;
  role: "user" | "assistant";
  text: string;
  images: string[];
  toolCalls: ChatToolCall[];
  createdUtc: string;
}

export interface ChatAskRequest {
  conversationId: string | null;
  question: string;
  images: string[];
}

/** The ask stream's first frame: which conversation the turn landed in (minted when the request named none). */
export interface ChatStreamConversation {
  id: string;
  title: string;
  userMessageId: number;
  userMessageOrdinal: number;
}

export interface ChatStreamDelta {
  text: string;
}

/** The ask stream's terminal frame: the persisted assistant message. */
export interface ChatStreamDone {
  messageId: number;
  ordinal: number;
  text: string;
  toolCalls: ChatToolCall[];
}

export interface ChatStreamError {
  message: string;
}

export interface ChatTranscription {
  text: string;
}
