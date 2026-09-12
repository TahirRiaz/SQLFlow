// Thin, typed wrappers over the control plane's /api/v1 surface: one function per endpoint, nothing else.
// Auth, error shaping, and rate-limit handling live in client.ts; pages compose these with TanStack Query.

import { del, get, getAnonymous, getText, post, postAnonymous, postBinary, put, streamSse, type QueryParams, type SseFrame } from "./client";
import type {
  AccessToken, AllSearchResult, Attention, AuthProviders,
  ChatAskRequest, ChatCapabilities, ChatConversation, ChatConversationsPurged, ChatMessage, ChatTranscription,
  ColumnHit, ComputeTask, ComputeTaskAccepted, ComputeTaskRequest,
  DataStream, DataStreams, DispatchSnapshot,
  ComputeTaskSummary, CreateAccessTokenRequest, CreateNotificationSubscriptionRequest, CreateScheduleRequest, CreatedAccessToken,
  CreateUserRequest, Dashboard, Datasource, DefinitionHit, DiscoveredFlow,
  FlowInsights, Recommendations, StepInsights,
  DiscoverRepoRequest, FileHit, FlowDependency, FlowColumnHit, FlowHit, StatementHit,
  FilePipelineMatch,
  LineageEdge, LineageObject, LineageObjectColumn, LineageObjectDetail, LineageProject, LineageSchema, MyNotificationOptions, Node, NodePurgeResult, NodeScript,
  ObjectDossier, PipelineBatch, SchemaKindCount, FileNode, FileFlows,
  ProjectGraph,
  GenerateNotificationDigestRequest, NotificationDelivery, NotificationDigest, NotificationDigestSummary,
  NotificationQueuedDelivery, NotificationSubscription, SendNotificationDigestRequest,
  ObjectHit, ObjectRepo, PagedResult,
  FlowParameters,
  PipelineColumn, PipelineDetail, PipelineFile, PipelineFileStats, PipelineSummary, RegisterRepoSourceRequest, Repo, RepoDeletionResult, RepoSource, RepoSourceRegistered, RepoSyncResult, RepoTree, Role,
  RunAssertion, RunDetail, RunFile, RunGroup, RunHealthCheckMetric, RunScope, RunScopePreview, RunStatement, RunTraceEntry, RunTraceStorage, RunTraceRetention, RunTraceRetentionUpdate, RunStatementPurgeResult, RunEventPurgeResult,
  RunSummary, RunSurrogateKey, SchemaChange, SchemaChangeDatabase, SchemaObjectCompare,
  RunTriggerAccepted, RunTriggerRequest, Schedule, ScheduleCreated, ScheduleDefinition, SchedulePlan, ScheduleRunAccepted, SessionResponse, TokenResponse,
  SourceDiscoverRequest, SourceDiscoverResult, Subscriber, SubscriberDossier, SubscriberHit,
  UpdateNotificationSubscriptionRequest, UpdateUserProfileRequest, User, Wave, WorkerPool, WorkerPoolScaleRequest,
} from "./types";

export interface PageQuery {
  page?: number;
  pageSize?: number;
}

// ---- Authentication ------------------------------------------------------------------------------------------------

export const authApi = {
  providers: () => getAnonymous<AuthProviders>("/api/v1/auth/providers"),
  login: (username: string, password: string) =>
    postAnonymous<SessionResponse>("/api/v1/auth/login", { username, password }),
  exchange: (token: string) => postAnonymous<SessionResponse>("/api/v1/auth/exchange", { token }),
  bootstrapToken: (secret: string, scopes: string[]) =>
    postAnonymous<TokenResponse>("/api/v1/auth/token", { secret, subject: null, scopes }),
  /** Trade the live session token for a fresh one; authenticated by the token it replaces. AuthContext drives this
   * on a timer so a working session never lapses under the user. Answers 401/403 once the session may no longer
   * roll (past the absolute cap, account deactivated, or a credential that does not renew at all). */
  renew: () => post<SessionResponse>("/api/v1/auth/renew"),
};

// ---- Dashboard ------------------------------------------------------------------------------------------------------

export const summaryApi = {
  get: () => get<Dashboard>("/api/v1/summary"),
};

// ---- Insights ------------------------------------------------------------------------------------------------------

export interface InsightsQuery {
  /** The analysis window in days (1-90; the server defaults flows/attention to 7). */
  days?: number;
  repoId?: string;
  batch?: string;
}

export const insightsApi = {
  /** Per-flow performance over the window, ordered by total processing time. */
  flows: (query: InsightsQuery & { limit?: number } = {}) =>
    get<FlowInsights>("/api/v1/insights/flows", query as QueryParams),
  /** The ranked "what needs attention" advisory list computed from the window's run history. */
  attention: (query: InsightsQuery & { limit?: number } = {}) =>
    get<Attention>("/api/v1/insights/attention", query as QueryParams),
  /** Run-history advisories merged with the newest warehouse DMV probe results. Compact by default;
   * includeSql=true carries the suggested statements (the GUI's expandable SQL). */
  recommendations: (query: InsightsQuery & { limit?: number; includeSql?: boolean } = {}) =>
    get<Recommendations>("/api/v1/insights/recommendations", query as QueryParams),
  /** One flow's step-level hotspots; includeSql=true adds one sample statement per step. */
  steps: (pipelineId: string, days?: number, includeSql?: boolean) =>
    get<StepInsights>(`/api/v1/insights/pipelines/${pipelineId}/steps`, { days, includeSql }),
};

// ---- DataStream anomaly detection -------------------------------------------------------------------------------------

export interface DataStreamQuery {
  /** The analysis window in days (1-180; the server defaults to 60). */
  days?: number;
  repoId?: string;
  batch?: string;
  /** Restrict to one verdict: stalled / degraded / watch / healthy / insufficient-history. */
  status?: string;
  /** Which side to analyse: "source" (vendor deliveries, the default), "internal" (our own processing),
   * or "all". They answer different questions and have different owners. */
  scope?: string;
  /** Count backfills as normal traffic. False by default: a history replay would otherwise redefine the
   * stream's normal and make every ordinary day after it look like a collapse. */
  includeBackfills?: boolean;
  /** Analyse streams that join no enabled schedule too. False by default: a flow nothing schedules has no
   * say in whether data is delivered, so holding it to a delivery expectation invents an incident. */
  includeUnscheduled?: boolean;
  limit?: number;
}

export const dataStreamApi = {
  /** The board: every stream's verdict, ranked most urgent first. No per-stream series (too large). */
  list: (query: DataStreamQuery = {}) => get<DataStreams>("/api/v1/datastreams", query as QueryParams),
  /** One stream in full: the day-by-day series the chart draws, and every detector's reasoning. */
  get: (pipelineId: string, days?: number, includeBackfills?: boolean) =>
    get<DataStream>(`/api/v1/datastreams/${pipelineId}`, { days, includeBackfills }),
};

// ---- Repos and pipelines ----------------------------------------------------------------------------------------------

export const repoApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Repo>>("/api/v1/repos", query as QueryParams),
  getById: (id: string) => get<Repo>(`/api/v1/repos/${id}`),
  // Everything the repository holds on its synced branch, not only the flows the catalog imported: the folder outline
  // the repo view lists its projects from. 400s for a repo with no git source and no reachable root path.
  tree: (id: string) => get<RepoTree>(`/api/v1/repos/${id}/tree`),
  // Re-sync a CLI/local-path repo from its recorded root path (connected: reads the live database for the derived
  // lineage tier). Refused server-side for a git-source-managed repo, which syncs via its source instead.
  syncLocal: (id: string) => post<RepoSyncResult>(`/api/v1/repos/${id}/sync`),
  // Delete a repo and everything attributed to it (pipelines, runs, lineage, schedules), plus any managed git source
  // registered under the same name so the background sync cannot recreate it. Irreversible; returns the counts removed.
  delete: (id: string) => del<RepoDeletionResult>(`/api/v1/repos/${id}`),
};

export interface PipelineListQuery extends PageQuery {
  repoId?: string;
  kind?: string;
  active?: boolean;
  name?: string;
  /** Root folder within the repo (the first path segment, or "(root)"); narrows the list to one source. */
  project?: string;
  /** Batch (source-system grouping) label; "default" also matches flows that declare no batch. */
  batch?: string;
}

export const pipelineApi = {
  list: (query: PipelineListQuery = {}) => get<PagedResult<PipelineSummary>>("/api/v1/pipelines", query as QueryParams),
  // The distinct projects (repo-root folders) for the list's project filter, optionally scoped to one repo.
  projects: (repoId?: string) =>
    get<string[]>("/api/v1/pipelines/projects", repoId ? { repoId } : {}),
  // The distinct batches (source-system groupings) with flow counts, optionally scoped to one repo; a flow
  // that declares no batch is coalesced into the "default" batch.
  batches: (repoId?: string) =>
    get<PipelineBatch[]>("/api/v1/pipelines/batches", repoId ? { repoId } : {}),
  getById: (id: string) => get<PipelineDetail>(`/api/v1/pipelines/${id}`),
  /** The run parameters that apply to this flow (kind + definition driven), for the dynamic trigger form. */
  parameters: (id: string) => get<FlowParameters>(`/api/v1/pipelines/${id}/parameters`),
  columns: (id: string) => get<PipelineColumn[]>(`/api/v1/pipelines/${id}/columns`),
  files: (id: string, query: PipelineFilesQuery = {}) =>
    get<PagedResult<PipelineFile>>(`/api/v1/pipelines/${id}/files`, query as QueryParams),
  /** The size profile of this flow's file deliveries (average, median, spread, extremes), computed over its
   * whole run history: the header's "what does a delivery from here normally weigh". */
  fileStats: (id: string) => get<PipelineFileStats>(`/api/v1/pipelines/${id}/files/stats`),
};

export interface PipelineFilesQuery extends PageQuery {
  search?: string;
}

// ---- Schema history ------------------------------------------------------------------------------------------------

export interface SchemaChangeQuery extends PageQuery {
  repoId?: string;
  database?: string;
  changeType?: string;
  /** ISO instant; only changes observed at or after it are returned. */
  since?: string;
  search?: string;
}

/** What the source-control snapshots found changing in the managed databases. */
export const schemaChangeApi = {
  list: (query: SchemaChangeQuery = {}) =>
    get<PagedResult<SchemaChange>>("/api/v1/schema-changes", query as QueryParams),
  databases: (query: { repoId?: string; since?: string } = {}) =>
    get<SchemaChangeDatabase[]>("/api/v1/schema-changes/databases", query as QueryParams),
  /**
   * The DDL behind one change row, at both ends of the window: the object's whole script as it stood when the
   * window opened against how it stands now. The row's id carries the object's identity, so no repository path
   * is passed from the browser; omitting `since` compares against the start of the recorded history.
   */
  compare: (id: number, query: { since?: string } = {}) =>
    get<SchemaObjectCompare>(`/api/v1/schema-changes/${id}/compare`, query as QueryParams),
};

// ---- Runs ----------------------------------------------------------------------------------------------------------------

export interface RunListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  flowKind?: string;
  status?: string;
  success?: boolean;
  flowName?: string;
  /** Exact batch (source-system grouping) label; the board's batch dropdown supplies a real label. */
  batch?: string;
  /** Keep only the runs of this schedule's member flows (the board's schedule dropdown), by membership. */
  scheduleId?: string;
  /** Keep only this run group's members (the group view), returned in wave order. */
  groupId?: string;
  /** Keep only each pipeline's newest run (the batch status board); status filters apply to that latest run. */
  latest?: boolean;
  /** Inclusive lower bound on WrittenUtc (ISO 8601, UTC): the schedules timeline reads runs by day. */
  from?: string;
  /** Inclusive upper bound on WrittenUtc (ISO 8601, UTC): the schedules timeline reads runs by day. */
  to?: string;
}

export interface RunScopePreviewQuery {
  repoId: string;
  flowName?: string;
  scope: RunScope;
  batch?: string;
  /** Node scope's "find all": preview with mode: manual and mode: disabled descendants included. */
  includeAll?: boolean;
}

export const runApi = {
  list: (query: RunListQuery = {}) => get<PagedResult<RunSummary>>("/api/v1/runs", query as QueryParams),
  getById: (runId: string) => get<RunDetail>(`/api/v1/runs/${runId}`),
  files: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunFile>>(`/api/v1/runs/${runId}/files`, query as QueryParams),
  assertions: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunAssertion>>(`/api/v1/runs/${runId}/assertions`, query as QueryParams),
  statements: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunStatement>>(`/api/v1/runs/${runId}/statements`, query as QueryParams),
  trace: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunTraceEntry>>(`/api/v1/runs/${runId}/trace`, query as QueryParams),
  /** The whole trace rendered as one plain-text document (the Copy-trace surface; also handy for tickets). */
  traceText: (runId: string) => getText(`/api/v1/runs/${runId}/trace/text`),
  /** The live trace as SSE: `entry` frames while the run executes, one `end` frame at its terminal status.
   * The cursors resume a dropped connection without replaying entries the caller already holds. */
  streamTrace: (
    runId: string,
    cursors: { afterEventId?: number; afterStatementId?: number },
    onFrame: (frame: SseFrame) => void,
    signal: AbortSignal,
    onOpen?: () => void,
  ) => streamSse(`/api/v1/runs/${runId}/trace/stream`, cursors, onFrame, signal, onOpen),
  surrogateKeys: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunSurrogateKey>>(`/api/v1/runs/${runId}/surrogate-keys`, query as QueryParams),
  healthMetrics: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunHealthCheckMetric>>(`/api/v1/runs/${runId}/health-metrics`, query as QueryParams),
  trigger: (request: RunTriggerRequest) => post<RunTriggerAccepted>("/api/v1/runs", request),
  cancel: (runId: string) => post<RunTriggerAccepted>(`/api/v1/runs/${runId}/cancel`),
  previewScope: (query: RunScopePreviewQuery) =>
    get<RunScopePreview>("/api/v1/runs/preview", query as unknown as QueryParams),
  group: (groupId: string) => get<RunGroup>(`/api/v1/runs/groups/${groupId}`),
  /** The live run group as SSE: a `member` frame (a RunSummary) whenever a member's summary changes (status,
   * last action, rows, timing), a full snapshot on connect, one `end` frame once every member is terminal. */
  streamGroup: (groupId: string, onFrame: (frame: SseFrame) => void, signal: AbortSignal, onOpen?: () => void) =>
    streamSse(`/api/v1/runs/groups/${groupId}/stream`, undefined, onFrame, signal, onOpen),
  cancelGroup: (groupId: string) => post<RunTriggerAccepted>(`/api/v1/runs/groups/${groupId}/cancel`),
};

// ---- Activity trace (generic operation trace: repo sync, lineage, ...) ------------------------------------------

export const activityApi = {
  /** The live trace of one operation as SSE: `entry` frames per new log line for the given (kind, subject), then a
   * single `end` frame once the newest line is terminal (or "idle" when the subject has no trace yet). The `afterId`
   * cursor resumes a dropped connection without replaying lines the caller already holds. One stream serves every
   * operation kind, so the bottom trace panel tails a sync, a lineage compute, and so on through the same call. */
  streamTrace: (
    kind: string,
    subject: string,
    cursor: { afterId?: number },
    onFrame: (frame: SseFrame) => void,
    signal: AbortSignal,
    onOpen?: () => void,
  ) => streamSse("/api/v1/activities/stream", { kind, subject, ...cursor }, onFrame, signal, onOpen),
};

// ---- Schedules ---------------------------------------------------------------------------------------------------------------

export interface ScheduleListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  source?: string;
  enabled?: boolean;
  /** Free-text substring of the schedule name, matched server-side so it spans every page, not just the one shown. */
  search?: string;
}

export const scheduleApi = {
  list: (query: ScheduleListQuery = {}) => get<PagedResult<Schedule>>("/api/v1/schedules", query as QueryParams),
  getById: (id: string) => get<Schedule>(`/api/v1/schedules/${id}`),
  // The cadence plus the flows a fire runs, in wave order, from the same expander the fire uses: the pre-flight
  // run board reads this to show "what runs, in which order" before Start is pressed.
  plan: (id: string) => get<SchedulePlan>(`/api/v1/schedules/${id}/plan`),
  // The YAML that defines the cadence: the declaring flow's document, or the schedules.yaml library file.
  definition: (id: string) => get<ScheduleDefinition>(`/api/v1/schedules/${id}/definition`),
  create: (request: CreateScheduleRequest) => post<ScheduleCreated>("/api/v1/schedules", request),
  // Fire the schedule now, on demand (to test it): enqueues a run of its flow without moving the next scheduled fire.
  // Fire the schedule now. Optional batches narrow the fire to members carrying those batch: tags ("run the
  // nightly, but only the small and medium tables"); repeated as ?batch=a&batch=b. An empty/omitted list runs
  // every member. A filter can only ever select a subset of the schedule's own members.
  // An optional from/to window turns the fire into a backfill: the schedule re-processes the source for that date
  // range (its integration roots re-land the slice, its silver flows re-pull from the source minimum).
  // chain=false keeps the fire to this schedule alone; the schedules chained behind it do not follow. Omitted means
  // the chain runs, matching what a clock-driven fire does.
  runNow: (
    id: string,
    batches?: string[],
    from?: string | null,
    to?: string | null,
    chain?: boolean,
  ) => {
    const params = new URLSearchParams();
    for (const b of batches ?? []) {
      params.append("batch", b);
    }
    if (from) {
      params.set("from", from);
    }
    if (to) {
      params.set("to", to);
    }
    if (chain === false) {
      params.set("chain", "false");
    }
    const query = params.toString() === "" ? "" : `?${params.toString()}`;
    return post<ScheduleRunAccepted>(`/api/v1/schedules/${id}/run${query}`);
  },
  pause: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/pause`),
  resume: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/resume`),
  remove: (id: string) => del<void>(`/api/v1/schedules/${id}`),
};

// ---- Nodes --------------------------------------------------------------------------------------------------------------------

export const nodeApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Node>>("/api/v1/nodes", query as QueryParams),
  /** Every worker pool's desired state and resolved replica target. */
  listPools: () => get<WorkerPool[]>("/api/v1/nodes/pools"),
  /** Set a pool's always-on floor and/or bounded manual override; returns the pool's new resolved state. */
  scalePool: (request: WorkerPoolScaleRequest) => put<WorkerPool>("/api/v1/nodes/pools/scale", request),
  /** Ask a node to drain and restart; the orchestrator recreates it. */
  restart: (name: string) => post<void>(`/api/v1/nodes/${encodeURIComponent(name)}/restart`),
  /** Remove a node from the fleet registry (a dead entry); a live node re-registers on its next heartbeat. */
  delete: (name: string) => del<void>(`/api/v1/nodes/${encodeURIComponent(name)}`),
  /** Drop every offline node from the fleet registry at once; returns how many entries were removed. */
  purgeOffline: () => del<NodePurgeResult>("/api/v1/nodes/offline"),
};

// ---- Dispatch -----------------------------------------------------------------------------------------------------------------

export const dispatchApi = {
  /** The dispatcher's own view of the queue: every queued run with the gate holding it back, every lease, the fleet,
   *  ownership and the last housekeeping passes. */
  snapshot: () => get<DispatchSnapshot>("/api/v1/dispatch"),
};

// ---- Datasources and ad-hoc compute ---------------------------------------------------------------------------------

export interface ComputeTaskListQuery extends PageQuery {
  status?: string;
  reference?: string;
  operation?: string;
}

export const datasourceApi = {
  list: () => get<Datasource[]>("/api/v1/datasources"),
  tasks: (query: ComputeTaskListQuery = {}) =>
    get<PagedResult<ComputeTaskSummary>>("/api/v1/datasources/tasks", query as QueryParams),
  /** One task; waitMs long-polls the server (capped at 20s) so a result arrives in one round trip. */
  task: (taskId: string, waitMs?: number, signal?: AbortSignal) =>
    get<ComputeTask>(`/api/v1/datasources/tasks/${taskId}`, { waitMs }, signal),
  createTask: (request: ComputeTaskRequest) => post<ComputeTaskAccepted>("/api/v1/datasources/tasks", request),
  cancelTask: (taskId: string) => post<ComputeTaskAccepted>(`/api/v1/datasources/tasks/${taskId}/cancel`),
};

/**
 * Runs one compute task end to end: enqueue, then long-poll until it reaches a terminal state. Resolves with
 * the terminal task (the caller inspects status/result/error); rejects only on transport/auth errors or when
 * the signal aborts. The abort signal also best-effort cancels the server-side task so an abandoned browse
 * never keeps a worker busy.
 */
export async function executeComputeTask(request: ComputeTaskRequest, signal?: AbortSignal): Promise<ComputeTask> {
  const accepted = await datasourceApi.createTask(request);
  try {
    for (;;) {
      const task = await datasourceApi.task(accepted.taskId, 10_000, signal);
      if (task.status === "succeeded" || task.status === "failed" || task.status === "cancelled" || task.status === "skipped") {
        return task;
      }
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      // The caller walked away (unmounted, changed scope): stop the server-side work too, best-effort.
      datasourceApi.cancelTask(accepted.taskId).catch(() => undefined);
    }

    throw error;
  }
}

// ---- Repo sources -------------------------------------------------------------------------------------------------------------

export const repoSourceApi = {
  list: (query: PageQuery = {}) => get<PagedResult<RepoSource>>("/api/v1/repos/sources", query as QueryParams),
  register: (request: RegisterRepoSourceRequest) => post<RepoSourceRegistered>("/api/v1/repos/sources", request),
  syncNow: (id: string) => post<RepoSource>(`/api/v1/repos/sources/${id}/sync`),
  // Preview-first scan: list a repo's flows without importing (nothing reaches the catalog until a sync).
  discover: (request: DiscoverRepoRequest) => post<DiscoveredFlow[]>("/api/v1/repos/discover", request),
  // Remove a source-only registration (one registered but not yet synced, so no repo exists to delete through).
  // Once a sync has produced the repo, deleting the repo removes the source instead.
  remove: (id: string) => del<void>(`/api/v1/repos/sources/${id}`),
};

// ---- Source discovery (JSON/XML flatten formula) -------------------------------------------------------------------------------

export const sourceApi = {
  /** Scan a JSON/XML file or folder, report its path structure, and generate the ingestion (flatten) YAML. */
  discover: (request: SourceDiscoverRequest) => post<SourceDiscoverResult>("/api/v1/sources/discover", request),
};

// ---- Lineage -------------------------------------------------------------------------------------------------------------------

export interface LineageObjectQuery extends PageQuery {
  name?: string;
  serverRef?: string;
  database?: string;
  schema?: string;
  kind?: string;
}

export interface FlowDependencyQuery extends PageQuery {
  /** Keep only the dependency edges touching this flow, in either direction (its "waits for" and "unblocks"). */
  pipelineId?: string;
}

export interface LineageEdgeQuery extends PageQuery {
  pipelineId?: string;
  objectKey?: string;
  relation?: string;
  tier?: string;
}

export const lineageApi = {
  // The whole (server, database, schema) hierarchy with object counts in one response, for tree skeletons.
  schemas: (query: { serverRef?: string; database?: string } = {}) =>
    get<LineageSchema[]>("/api/v1/lineage/schemas", query as QueryParams),
  // The same hierarchy broken down by object kind (Tables/Views/Procedures/...), also unpaged and bounded.
  schemaKinds: (query: { serverRef?: string; database?: string; schema?: string } = {}) =>
    get<SchemaKindCount[]>("/api/v1/lineage/schemas/kinds", query as QueryParams),
  // Every file endpoint decomposed to its canonical parent (origin/container/folder/leaf), for the source
  // tree. Unpaged and bounded: file nodes are the distinct declared locations, not physical blobs.
  fileTree: () => get<FileNode[]>("/api/v1/lineage/file-tree"),
  // A file source's provenance: the pipelines that produce it and consume it, with where consumers land data.
  fileFlows: (key: string) => get<FileFlows>("/api/v1/lineage/file-flows", { key }),
  objects: (query: LineageObjectQuery = {}) =>
    get<PagedResult<LineageObject>>("/api/v1/lineage/objects", query as QueryParams),
  objectDetail: (key: string) => get<LineageObjectDetail>("/api/v1/lineage/objects/detail", { key }),
  // Everything known about one object in a single payload: identity, columns, and its lineage edges.
  dossier: (key: string) => get<ObjectDossier>("/api/v1/lineage/objects/dossier", { key }),
  // The code behind a node: an object's SQL or a flow's YAML; view "object" resolves a flow key to the SQL of
  // the database object it executes (an sp flow's procedure) instead of its YAML.
  script: (key: string, view?: "object") =>
    get<NodeScript>("/api/v1/lineage/script", view === undefined ? { key } : { key, view }),
  objectColumns: (key: string, query: PageQuery = {}) =>
    get<PagedResult<LineageObjectColumn>>("/api/v1/lineage/objects/columns", { key, ...query } as QueryParams),
  objectRepos: (key: string) => get<ObjectRepo[]>("/api/v1/lineage/objects/repos", { key }),
  // Who consumes the warehouse: the reports, workbooks, and applications declared in a subscribers.yaml.
  subscribers: (query: { type?: string; search?: string } = {}) =>
    get<Subscriber[]>("/api/v1/lineage/subscribers", query as QueryParams),
  // What one subscriber consumes: its queries, and every object those queries read.
  subscriberDossier: (key: string) =>
    get<SubscriberDossier>("/api/v1/lineage/subscribers/dossier", { key }),
  filePipelines: (file: string) => get<FilePipelineMatch[]>("/api/v1/lineage/file-pipelines", { file }),
  edges: (repoId: string, query: LineageEdgeQuery = {}) =>
    get<PagedResult<LineageEdge>>(`/api/v1/repos/${repoId}/lineage/edges`, query as QueryParams),
  waves: (repoId: string) => get<Wave[]>(`/api/v1/repos/${repoId}/waves`),
  dependencies: (repoId: string, query: FlowDependencyQuery = {}) =>
    get<PagedResult<FlowDependency>>(`/api/v1/repos/${repoId}/dependencies`, query as QueryParams),
  // Every selectable (repo, project) pair the lineage graph can be scoped to, for the searchable scope picker.
  projects: () => get<LineageProject[]>("/api/v1/lineage/projects"),
  // A project's cross-repo downstream lineage closure. `expand` re-seeds the walk from frontier nodes (an object
  // key or a pipeline id) and is sent as repeated query params, which the single-value query builder cannot do, so
  // it is folded into the path here; the rest ride the normal query object.
  projectGraph: (params: { repoId?: string; project?: string; depth?: number; expand?: string[] }) => {
    const expand = params.expand ?? [];
    const suffix = expand.length > 0
      ? `?${expand.map((token) => `expand=${encodeURIComponent(token)}`).join("&")}`
      : "";
    return get<ProjectGraph>(
      `/api/v1/lineage/project-graph${suffix}`,
      { repoId: params.repoId, project: params.project, depth: params.depth } as QueryParams,
    );
  },
};

// ---- Search --------------------------------------------------------------------------------------------------------------------

export const searchApi = {
  all: (q: string) => get<AllSearchResult>("/api/v1/search/all", { q }),
  objects: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ObjectHit>>("/api/v1/search/objects", { name, ...query } as QueryParams),
  columns: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ColumnHit>>("/api/v1/search/columns", { name, ...query } as QueryParams),
  definitions: (q: string, query: PageQuery = {}) =>
    get<PagedResult<DefinitionHit>>("/api/v1/search/definitions", { q, ...query } as QueryParams),
  files: (name: string, query: PageQuery = {}) =>
    get<PagedResult<FileHit>>("/api/v1/search/files", { name, ...query } as QueryParams),
  flows: (q: string, query: PageQuery = {}) =>
    get<PagedResult<FlowHit>>("/api/v1/search/flows", { q, ...query } as QueryParams),
  flowColumns: (name: string, query: PageQuery = {}) =>
    get<PagedResult<FlowColumnHit>>("/api/v1/search/flow-columns", { name, ...query } as QueryParams),
  statements: (q: string, query: PageQuery = {}) =>
    get<PagedResult<StatementHit>>("/api/v1/search/statements", { q, ...query } as QueryParams),
  subscribers: (q: string, query: PageQuery = {}) =>
    get<PagedResult<SubscriberHit>>("/api/v1/search/subscribers", { q, ...query } as QueryParams),
};

// ---- Users ---------------------------------------------------------------------------------------------------------------------

export interface UserListQuery extends PageQuery {
  username?: string;
  role?: string;
  provider?: string;
  active?: boolean;
}

export const userApi = {
  list: (query: UserListQuery = {}) => get<PagedResult<User>>("/api/v1/users", query as QueryParams),
  getById: (id: string) => get<User>(`/api/v1/users/${id}`),
  create: (request: CreateUserRequest) => post<User>("/api/v1/users", request),
  updateProfile: (id: string, request: UpdateUserProfileRequest) =>
    post<User>(`/api/v1/users/${id}/profile`, request),
  remove: (id: string) => del<void>(`/api/v1/users/${id}`),
  setRole: (id: string, role: string) => post<User>(`/api/v1/users/${id}/role`, { role }),
  activate: (id: string) => post<User>(`/api/v1/users/${id}/activate`),
  deactivate: (id: string) => post<User>(`/api/v1/users/${id}/deactivate`),
  setPassword: (id: string, password: string) => post<User>(`/api/v1/users/${id}/password`, { password }),
  roles: () => get<Role[]>("/api/v1/roles"),
};

// ---- Personal access tokens (self-service: the caller's own tokens) ----------------------------------------------

export const tokenApi = {
  list: () => get<AccessToken[]>("/api/v1/me/tokens"),
  create: (request: CreateAccessTokenRequest) => post<CreatedAccessToken>("/api/v1/me/tokens", request),
  revoke: (id: string) => del<void>(`/api/v1/me/tokens/${id}`),
};

// ---- Notifications (self-service: the caller's own subscriptions and deliveries) ---------------------------------

export const notificationApi = {
  options: () => get<MyNotificationOptions>("/api/v1/me/notifications/options"),
  listSubscriptions: () => get<NotificationSubscription[]>("/api/v1/me/notifications/subscriptions"),
  createSubscription: (request: CreateNotificationSubscriptionRequest) =>
    post<NotificationSubscription>("/api/v1/me/notifications/subscriptions", request),
  updateSubscription: (id: string, request: UpdateNotificationSubscriptionRequest) =>
    put<NotificationSubscription>(`/api/v1/me/notifications/subscriptions/${id}`, request),
  deleteSubscription: (id: string) => del<void>(`/api/v1/me/notifications/subscriptions/${id}`),
  testSubscription: (id: string) =>
    post<NotificationQueuedDelivery>(`/api/v1/me/notifications/subscriptions/${id}/test`),
  listDeliveries: (take = 50) => get<NotificationDelivery[]>("/api/v1/me/notifications/deliveries", { take }),
  // Estate digests are not per-user: one record of what the estate did in each window, readable by anyone.
  listDigests: (take = 50) => get<NotificationDigestSummary[]>("/api/v1/notifications/digests", { take }),
  getDigest: (id: string) => get<NotificationDigest>(`/api/v1/notifications/digests/${id}`),
  generateDigest: (request: GenerateNotificationDigestRequest) =>
    post<NotificationDigest>("/api/v1/notifications/digests", request),
  sendDigest: (id: string, request: SendNotificationDigestRequest) =>
    post<NotificationQueuedDelivery>(`/api/v1/notifications/digests/${id}/send`, request),
};

// ---- Maintenance (run-trace storage retention) ------------------------------------------------------------------

// ---- Chat assistant (per-user conversations, streamed answers) ---------------------------------------------------

export const chatApi = {
  capabilities: () => get<ChatCapabilities>("/api/v1/chat/capabilities"),
  listConversations: () => get<ChatConversation[]>("/api/v1/chat/conversations"),
  renameConversation: (id: string, title: string) =>
    put<ChatConversation>(`/api/v1/chat/conversations/${id}`, { title }),
  deleteConversation: (id: string) => del<void>(`/api/v1/chat/conversations/${id}`),
  /** Deletes every conversation of the signed-in user and returns how much was removed. */
  deleteAllConversations: () => del<ChatConversationsPurged>("/api/v1/chat/conversations"),
  messages: (id: string) => get<ChatMessage[]>(`/api/v1/chat/conversations/${id}/messages`),
  /** One question, answered as SSE: a `conversation` frame, then `tool`/`delta` frames, then `done` (or `error`). */
  ask: (request: ChatAskRequest, onFrame: (frame: SseFrame) => void, signal: AbortSignal, onOpen?: () => void) =>
    streamSse("/api/v1/chat/ask", undefined, onFrame, signal, onOpen, { method: "POST", body: request }),
  /** Sends a voice recording (as recorded by MediaRecorder) and returns its transcription. */
  transcribe: (audio: Blob, fileName: string, signal?: AbortSignal) =>
    postBinary<ChatTranscription>("/api/v1/chat/transcribe", audio, audio.type || "audio/webm", { fileName }, signal),
};

export const maintenanceApi = {
  traceStorage: () => get<RunTraceStorage>("/api/v1/maintenance/trace-storage"),
  /** Set how many days to keep superseded SQL statements, or null to keep forever (age-based pruning off). */
  setStatementRetention: (retentionDays: number | null) =>
    put<RunTraceRetention>("/api/v1/maintenance/statement-retention", { retentionDays } satisfies RunTraceRetentionUpdate),
  /** Delete all generated SQL statements now and return how many rows were deleted; run events are never touched. */
  purgeStatements: () => post<RunStatementPurgeResult>("/api/v1/maintenance/statements/purge"),
  /** Delete all run events now and return how many rows were deleted; the deliberate escape hatch (events are never auto-pruned). */
  purgeEvents: () => post<RunEventPurgeResult>("/api/v1/maintenance/events/purge"),
};
