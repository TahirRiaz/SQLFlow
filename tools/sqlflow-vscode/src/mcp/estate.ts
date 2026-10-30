import { McpServer } from './mcpServer';

/** A page of results from the control plane's PagedResult<T> shape. */
export interface Paged<T> {
    items: T[];
    page: number;
    pageSize: number;
    total: number;
}

/** The links a tool's answer carries: `page` is the row's own GUI page, the rest are named for what they open. */
export interface WithLinks {
    links?: Record<string, string>;
}

export interface Repo extends WithLinks {
    id: string;
    name: string;
    remoteUrl?: string | null;
    rootPath?: string | null;
    lastSyncUtc?: string;
}

export interface RepoSource {
    id: string;
    name: string;
    remoteUrl?: string | null;
    localPath?: string | null;
    branch: string;
    enabled: boolean;
    lastSyncUtc?: string | null;
    lastSyncedSha?: string | null;
    lastError?: string | null;
    syncPending?: boolean;
}

export interface Pipeline extends WithLinks {
    id: string;
    repoId: string;
    name: string;
    kind: string;
    batch?: string | null;
    wave?: number;
    active?: boolean;
    lifecycle?: string;
    executionMode?: string;
    sourceServer?: string | null;
    targetServer?: string | null;
    relativePath?: string;
    lastSeenUtc?: string;
}

export interface PipelineDetail extends Pipeline {
    yaml: string;
    contentHash?: string;
    runsOnSchedule?: boolean;
}

export interface PipelineColumn {
    columnName: string;
    dataType?: string | null;
}

export interface Run extends WithLinks {
    runId: string;
    pipelineId: string;
    repoId?: string | null;
    flowName: string;
    flowKind?: string;
    status: string;
    success?: boolean;
    commitSha?: string | null;
    operation?: string | null;
    requestedBy?: string | null;
    triggerSource?: string | null;
    startUtc?: string | null;
    endUtc?: string | null;
    writtenUtc?: string;
    durationSeconds?: number | null;
    rowsLoaded?: number | null;
    rowsInserted?: number | null;
    rowsUpdated?: number | null;
    rowsDeleted?: number | null;
    fileCount?: number;
    lastAction?: string | null;
    error?: string | null;
    failedStatementStep?: string | null;
    failedStatementSql?: string | null;
}

export interface RunTraceEntry {
    kind: string;
    ordinal: number;
    timestampUtc?: string | null;
    level: string;
    step?: string | null;
    message?: string | null;
    sql?: string | null;
    error?: string | null;
    rows?: number | null;
    elapsedMs?: number | null;
}

export interface RunStatement {
    ordinal: number;
    step: string;
    sql: string;
    error?: string | null;
}

export interface RunAssertion {
    name: string;
    result: string;
    assertedValue: string;
    evaluated: boolean;
    error?: string | null;
}

export interface RunFile {
    name: string;
    path?: string | null;
    rows: number;
    sizeBytes: number;
}

export interface Schedule extends WithLinks {
    id: string;
    repoId: string;
    name?: string;
    cron?: string | null;
    intervalSeconds?: number | null;
    timezone?: string;
    enabled?: boolean;
    paused?: boolean;
    nextFireUtc?: string | null;
    lastFireUtc?: string | null;
    memberPipelineIds?: string[];
    afterSchedules?: string[] | null;
}

/** One flow-to-flow step of a repository's order: `toFlow` waits for `fromFlow`, through the objects they share. */
export interface FlowDependency {
    fromFlow: string;
    toFlow: string;
    fromPipelineId: string;
    toPipelineId: string;
    viaObjects?: string;
}

export interface Datasource {
    reference: string;
    kind?: string | null;
    resolvable: boolean;
    sourcePipelines: number;
    targetPipelines: number;
}

/** Where an AI context stands, as an admin sees it. */
export interface AiContext {
    name: string;
    status: string;
    currentVersion?: string | null;
    approvedVersion?: string | null;
    approvedBy?: string | null;
    approvedUtc?: string | null;
    tableCount?: number;
    servedTableCount?: number;
    problemCount?: number;
}

export interface FlowKindOperation {
    name: string;
    label: string;
    description: string;
    writesTarget: boolean;
}

export interface FlowKinds {
    flowKinds: string[];
    documentKinds: string[];
    runOperations?: { flowType: string; description: string; operations: FlowKindOperation[]; defaultOperation?: string | null }[];
}

export interface DocumentProblem {
    severity: string;
    line: number;
    column: number;
    code?: string | null;
    message: string;
}

/** One data stream's verdict: a flow, the table it writes, and whether data is still arriving the way its learned
 * pattern says it should. */
export interface DataStream {
    pipelineId: string;
    flowName: string;
    targetObject?: string | null;
    batch?: string | null;
    status: 'stalled' | 'degraded' | 'watch' | 'healthy' | 'insufficient-history';
    category: string;
    severity: 'critical' | 'warning' | 'info';
    agreeingDetectors: number;
    summary: string;
    profile: {
        pattern: { shape: string; description: string; typicalRows: number };
        daysSinceLastLoad: number | null;
        unexpectedNullDays: number;
        [k: string]: unknown;
    };
    [k: string]: unknown;
}

/** The data-stream board, ranked most urgent first, with the counts covering every analysed stream. */
export interface DataStreams {
    windowDays: number;
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

/** How long a read of the catalog is reused. Lenses and views ask together as a person moves between files. */
const MEMO_MS = 15_000;

/** Unwrap either a PagedResult<T> or a bare T[] into T[]. */
export function items<T>(result: { items: T[] } | T[]): T[] {
    return Array.isArray(result) ? result : result.items;
}

/**
 * The estate as this extension reads it: named reads over the server's tools, typed to the answers they give. Reads a
 * person's movement repeats are memoised for a moment, and any action drops the memo.
 */
export class Estate {
    private readonly memo = new Map<string, { at: number; value: Promise<unknown> }>();
    readonly mcp: McpServer;

    constructor(mcp: McpServer) {
        this.mcp = mcp;
    }

    forget(): void {
        this.memo.clear();
    }

    private remembered<T>(tool: string, args: Record<string, unknown> = {}): Promise<T> {
        const key = `${tool} ${JSON.stringify(args)}`;
        const held = this.memo.get(key);
        if (held && Date.now() - held.at < MEMO_MS) {
            return held.value as Promise<T>;
        }
        const value = this.mcp.call<T>(tool, args);
        this.memo.set(key, { at: Date.now(), value });
        value.catch(() => this.memo.delete(key));
        return value;
    }

    async repos(): Promise<Repo[]> {
        return items(await this.remembered<Paged<Repo> | Repo[]>('list_repos'));
    }

    async repoSources(): Promise<RepoSource[]> {
        return items(await this.remembered<Paged<RepoSource> | RepoSource[]>('list_repo_sources'));
    }

    async syncRepoSource(id: string): Promise<void> {
        await this.mcp.text('sync_repo_source', { id });
        this.forget();
    }

    async pipelines(query: { repoId?: string; kind?: string; active?: boolean; name?: string; pageSize?: number } = {}): Promise<Pipeline[]> {
        return items(await this.remembered<Paged<Pipeline> | Pipeline[]>('list_pipelines', { ...query, pageSize: query.pageSize ?? 200 }));
    }

    /** The catalog's flow of a name, the one at `relativePath` when several repositories hold one. */
    async pipelineNamed(name: string, relativePath?: string): Promise<Pipeline | undefined> {
        const named = (await this.pipelines({ name })).filter((pipeline) => pipeline.name === name);
        const normalised = relativePath?.replace(/\\/g, '/');
        return named.find((pipeline) => normalised !== undefined && pipeline.relativePath !== undefined
            && (normalised.endsWith(pipeline.relativePath) || pipeline.relativePath.endsWith(normalised))) ?? named[0];
    }

    pipeline(id: string): Promise<PipelineDetail> {
        return this.remembered('get_pipeline', { id });
    }

    async pipelineColumns(id: string): Promise<PipelineColumn[]> {
        return items(await this.remembered<Paged<PipelineColumn> | PipelineColumn[]>('pipeline_columns', { id }));
    }

    async runs(query: { pipelineId?: string; repoId?: string; status?: string; pageSize?: number } = {}): Promise<Run[]> {
        return items(await this.mcp.call<Paged<Run> | Run[]>('list_runs', { ...query, pageSize: query.pageSize ?? 50 }));
    }

    run(runId: string): Promise<Run> {
        return this.mcp.call('get_run', { runId });
    }

    /** The end of a run's trace, however long it is: the first read says how many entries there are. */
    async runTrace(runId: string, entries = 200): Promise<{ entries: RunTraceEntry[]; total: number }> {
        const first = await this.mcp.call<Paged<RunTraceEntry>>('run_trace', { runId, pageSize: entries });
        if (first.total <= first.items.length) {
            return { entries: first.items, total: first.total };
        }
        const page = Math.ceil(first.total / entries);
        const last = await this.mcp.call<Paged<RunTraceEntry>>('run_trace', { runId, page, pageSize: entries });
        return { entries: last.items, total: first.total };
    }

    async runStatements(runId: string): Promise<RunStatement[]> {
        return items(await this.mcp.call<Paged<RunStatement> | RunStatement[]>('run_statements', { runId }));
    }

    async runAssertions(runId: string): Promise<RunAssertion[]> {
        return items(await this.mcp.call<Paged<RunAssertion> | RunAssertion[]>('run_assertions', { runId }));
    }

    async runFiles(runId: string): Promise<RunFile[]> {
        return items(await this.mcp.call<Paged<RunFile> | RunFile[]>('run_files', { runId }));
    }

    async triggerRun(arguments_: {
        repoId: string;
        flowName: string;
        fullLoad?: boolean;
        operation?: string;
        values?: Record<string, unknown>;
        payload?: Record<string, unknown>;
    }): Promise<{ runId: string; status: string }> {
        const accepted = await this.mcp.call<{ runId: string; status: string }>('trigger_run', arguments_);
        this.forget();
        return accepted;
    }

    async cancelRun(runId: string): Promise<void> {
        await this.mcp.text('cancel_run', { runId });
    }

    async schedules(): Promise<Schedule[]> {
        return items(await this.remembered<Paged<Schedule> | Schedule[]>('list_schedules'));
    }

    async datasources(): Promise<Datasource[]> {
        return items(await this.remembered<Paged<Datasource> | Datasource[]>('list_datasources'));
    }

    async aiContexts(): Promise<AiContext[]> {
        return (await this.remembered<{ contexts: AiContext[] }>('list_ai_contexts')).contexts;
    }

    flowKinds(): Promise<FlowKinds> {
        return this.remembered('list_flow_kinds');
    }

    lineageWaves(repoId: string): Promise<unknown> {
        return this.remembered('lineage_waves', { repoId });
    }

    async dependencies(repoId: string, pipelineId: string): Promise<FlowDependency[]> {
        const answer = await this.remembered<FlowDependency[] | { items: FlowDependency[] }>('lineage_dependencies', { repoId, pipelineId });
        return Array.isArray(answer) ? answer : answer.items ?? [];
    }

    flowFileHistory(pipelineId: string): Promise<unknown> {
        return this.mcp.call('flow_definition_file_history', { pipelineId });
    }

    dataStreams(days: number): Promise<DataStreams> {
        return this.mcp.call('detect_stream_anomalies', { days });
    }

    dataStream(pipelineId: string, days: number): Promise<DataStream> {
        return this.mcp.call('detect_stream_anomalies', { pipelineId, days });
    }

    /** What the language engine reports of a document's text: the same analysis as the editor's, without opening it. */
    async validate(yaml: string): Promise<DocumentProblem[]> {
        const answer = await this.mcp.text('validate_flow', { yaml });
        if (answer.startsWith('OK')) {
            return [];
        }
        return (JSON.parse(answer) as { problems: DocumentProblem[] }).problems;
    }
}
