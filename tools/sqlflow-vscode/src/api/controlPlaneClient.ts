import * as vscode from 'vscode';
import { Session } from './session';

/** A page of results from the control plane's PagedResult<T> shape. */
export interface Paged<T> {
    items: T[];
    page: number;
    pageSize: number;
    total: number;
}

export interface Repo { id: string; name: string; [k: string]: unknown; }
export interface Pipeline {
    id: string; name: string; kind?: string; batch?: string; active?: boolean; repoId?: string; [k: string]: unknown;
}
export interface Run {
    runId: string; flowName?: string; status: string; startedUtc?: string; finishedUtc?: string; [k: string]: unknown;
}
export interface Schedule { id: string; name?: string; cron?: string; paused?: boolean; [k: string]: unknown; }

/** One data stream's verdict from `/api/v1/datastreams`: a flow, the table it writes, and whether data is
 * still arriving the way its learned pattern says it should. */
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

/**
 * Thin fetch-based client for the control plane's `/api/v1` surface. Every call
 * attaches the session bearer token; a 401 surfaces as a typed error so the
 * caller can prompt the user to sign in again.
 */
export class ControlPlaneClient {
    constructor(private readonly session: Session) {}

    baseUrl(): string {
        const url = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', 'http://localhost:8080');
        return url.replace(/\/+$/, '');
    }

    private async request<T>(path: string, init?: { method?: string; query?: Record<string, unknown>; body?: unknown }): Promise<T> {
        // Rotate the managed token if it is close to expiry, so a long-running session never lapses into a re-login.
        // A no-op unless a rotation is actually due.
        await this.session.ensureFresh(this.baseUrl());
        const token = await this.session.getToken();
        if (!token) {
            throw new AuthError('Not signed in. Run "SQLFlow: Sign In".');
        }
        const url = new URL(this.baseUrl() + path);
        for (const [k, v] of Object.entries(init?.query ?? {})) {
            if (v !== undefined && v !== null && v !== '') {
                url.searchParams.set(k, String(v));
            }
        }
        const resp = await fetch(url, {
            method: init?.method ?? 'GET',
            headers: {
                authorization: `Bearer ${token}`,
                ...(init?.body ? { 'content-type': 'application/json' } : {}),
            },
            body: init?.body ? JSON.stringify(init.body) : undefined,
        });
        if (resp.status === 401) {
            throw new AuthError('The control plane rejected the token; sign in again.');
        }
        if (!resp.ok) {
            const text = await resp.text().catch(() => '');
            throw new Error(`${path} returned ${resp.status}${text ? `: ${text}` : ''}`);
        }
        const text = await resp.text();
        return (text ? JSON.parse(text) : null) as T;
    }

    async health(): Promise<boolean> {
        try {
            const resp = await fetch(`${this.baseUrl()}/health/ready`);
            return resp.ok;
        } catch {
            return false;
        }
    }

    listRepos(): Promise<Paged<Repo>> {
        return this.request('/api/v1/repos');
    }

    listPipelines(query: { repoId?: string; kind?: string; active?: boolean; name?: string; pageSize?: number } = {}): Promise<Paged<Pipeline>> {
        return this.request('/api/v1/pipelines', { query: { ...query, pageSize: query.pageSize ?? 200 } });
    }

    getPipeline(id: string): Promise<Pipeline> {
        return this.request(`/api/v1/pipelines/${id}`);
    }

    pipelineDefinition(id: string): Promise<unknown> {
        return this.request(`/api/v1/pipelines/${id}/definition`);
    }

    listRuns(query: { repoId?: string; pipelineId?: string; status?: string; pageSize?: number } = {}): Promise<Paged<Run>> {
        return this.request('/api/v1/runs', { query: { ...query, pageSize: query.pageSize ?? 50 } });
    }

    getRun(runId: string): Promise<Run> {
        return this.request(`/api/v1/runs/${runId}`);
    }

    triggerRun(body: { repoId: string; flowName: string; fullLoad?: boolean }): Promise<{ runId: string; status: string }> {
        return this.request('/api/v1/runs', { method: 'POST', body });
    }

    cancelRun(runId: string): Promise<void> {
        return this.request(`/api/v1/runs/${runId}/cancel`, { method: 'POST', body: {} });
    }

    listSchedules(): Promise<Paged<Schedule> | Schedule[]> {
        return this.request('/api/v1/schedules');
    }

    /** Fire a schedule now, enqueuing a run of its flow (to test the schedule) without moving its cadence. */
    runSchedule(id: string): Promise<{ runId: string }> {
        return this.request(`/api/v1/schedules/${id}/run`, { method: 'POST', body: {} });
    }

    /** The data-stream board: which tables have stopped receiving data. Backfills are excluded from the
     * baseline by default, which is what stops a history replay redefining a stream's normal. */
    listDataStreams(query: { days?: number; status?: string; limit?: number } = {}): Promise<DataStreams> {
        return this.request('/api/v1/datastreams', { query: { ...query, limit: query.limit ?? 200 } });
    }

    /** One stream in full: the day-by-day series and every detector's reasoning. */
    getDataStream(pipelineId: string, days?: number): Promise<DataStream> {
        return this.request(`/api/v1/datastreams/${pipelineId}`, { query: { days } });
    }

    lineageEdges(repoId: string): Promise<unknown> {
        return this.request(`/api/v1/repos/${repoId}/lineage/edges`);
    }

    waves(repoId: string): Promise<unknown> {
        return this.request(`/api/v1/repos/${repoId}/waves`);
    }
}

/** Raised when the request is unauthenticated or the token was rejected. */
export class AuthError extends Error {}
