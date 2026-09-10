import * as vscode from 'vscode';
import { AuthError, ControlPlaneClient, DataStream, Pipeline, Repo, Run, Schedule } from '../api/controlPlaneClient';

/** Unwrap either a PagedResult<T> or a bare T[] into T[]. */
function items<T>(result: { items: T[] } | T[]): T[] {
    return Array.isArray(result) ? result : result.items;
}

/** A leaf node shown when the catalog cannot be loaded (e.g. not signed in). */
function messageNode(label: string, command?: vscode.Command): vscode.TreeItem {
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.command = command;
    item.iconPath = new vscode.ThemeIcon('info');
    return item;
}

export class RepoNode extends vscode.TreeItem {
    constructor(public readonly repo: Repo) {
        super(repo.name ?? repo.id, vscode.TreeItemCollapsibleState.Collapsed);
        this.contextValue = 'repo';
        this.iconPath = new vscode.ThemeIcon('repo');
        this.id = `repo:${repo.id}`;
    }
}

export class PipelineNode extends vscode.TreeItem {
    constructor(public readonly pipeline: Pipeline) {
        super(pipeline.name ?? pipeline.id, vscode.TreeItemCollapsibleState.None);
        this.contextValue = 'pipeline';
        this.description = [pipeline.kind, pipeline.batch].filter(Boolean).join(' · ');
        this.iconPath = new vscode.ThemeIcon(pipeline.active === false ? 'circle-slash' : 'symbol-event');
        this.tooltip = `${pipeline.name}\nkind: ${pipeline.kind ?? 'file'}\nactive: ${pipeline.active ?? true}`;
        this.command = { command: 'sqlflow.openPipeline', title: 'Open', arguments: [this] };
    }
}

export class CatalogTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        try {
            if (!element) {
                const repos = items(await this.client.listRepos());
                return repos.length ? repos.map((r) => new RepoNode(r)) : [messageNode('No repositories synced yet.')];
            }
            if (element instanceof RepoNode) {
                const pipelines = items(await this.client.listPipelines({ repoId: element.repo.id }));
                // Stamp the owning repo so a run can be triggered straight from the node.
                pipelines.forEach((p) => (p.repoId = p.repoId ?? element.repo.id));
                return pipelines.length ? pipelines.map((p) => new PipelineNode(p)) : [messageNode('No flows in this repository.')];
            }
            return [];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class RunNode extends vscode.TreeItem {
    constructor(public readonly run: Run) {
        super(run.flowName ?? run.runId, vscode.TreeItemCollapsibleState.None);
        const active = run.status === 'queued' || run.status === 'running';
        this.contextValue = active ? 'run-active' : 'run';
        this.description = run.status;
        this.iconPath = new vscode.ThemeIcon(statusIcon(run.status));
        this.tooltip = `${run.flowName ?? ''}\nstatus: ${run.status}\nstarted: ${run.startedUtc ?? '-'}`;
        this.command = { command: 'sqlflow.openRun', title: 'Open', arguments: [this] };
    }
}

export class RunsTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const limit = vscode.workspace.getConfiguration('sqlflow').get<number>('runResultLimit', 50);
            const runs = items(await this.client.listRuns({ pageSize: limit }));
            return runs.length ? runs.map((r) => new RunNode(r)) : [messageNode('No runs yet.')];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class SchedulesTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const schedules = items(await this.client.listSchedules());
            return schedules.length
                ? schedules.map((s) => new ScheduleNode(s))
                : [messageNode('No schedules configured.')];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class ScheduleNode extends vscode.TreeItem {
    constructor(public readonly schedule: Schedule) {
        super(schedule.name ?? schedule.id, vscode.TreeItemCollapsibleState.None);
        this.description = [schedule.cron, schedule.paused ? 'paused' : undefined].filter(Boolean).join(' · ');
        this.iconPath = new vscode.ThemeIcon(schedule.paused ? 'debug-pause' : 'watch');
        this.contextValue = 'schedule';
    }
}

export class DataStreamNode extends vscode.TreeItem {
    constructor(public readonly stream: DataStream) {
        super(stream.flowName, vscode.TreeItemCollapsibleState.None);
        this.contextValue = 'datastream';
        this.description = describeStream(stream);
        this.iconPath = new vscode.ThemeIcon(streamIcon(stream.status), streamColor(stream.severity));
        this.tooltip = new vscode.MarkdownString(
            `**${stream.flowName}**\n\n` +
            `${stream.targetObject ?? stream.batch ?? 'unknown target'}\n\n` +
            `${stream.summary}\n\n` +
            `_${stream.profile.pattern.description}_`
        );
        this.id = `datastream:${stream.pipelineId}`;
        this.command = { command: 'sqlflow.openDataStream', title: 'Open', arguments: [this] };
    }
}

/**
 * The streams that need attention, most urgent first. Healthy streams are deliberately NOT listed: a sidebar
 * showing three hundred rows of "fine" is a sidebar nobody reads, and the count of them rides in a trailing
 * node so the view still says how much it looked at.
 */
export class DataStreamsTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const config = vscode.workspace.getConfiguration('sqlflow');
            const days = config.get<number>('dataStreamWindowDays', 60);
            const showHealthy = config.get<boolean>('dataStreamShowHealthy', false);
            const board = await this.client.listDataStreams({ days });

            const listed = showHealthy
                ? board.streams
                : board.streams.filter((s) => s.status !== 'healthy' && s.status !== 'insufficient-history');
            if (listed.length === 0) {
                const label = board.analyzedStreams === 0
                    ? 'No flow has run in this window.'
                    : `All ${board.analyzedStreams} streams are loading on pattern.`;
                return [messageNode(label)];
            }

            const nodes: vscode.TreeItem[] = listed.map((s) => new DataStreamNode(s));
            const hidden = board.analyzedStreams - listed.length;
            if (hidden > 0) {
                nodes.push(messageNode(`${hidden} more on pattern or too new to judge.`));
            }

            return nodes;
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

/** The verdict a row carries beside its name: what is wrong, and the number that says so. */
function describeStream(stream: DataStream): string {
    const days = stream.profile.daysSinceLastLoad;
    switch (stream.category) {
        case 'stalled':
            return days === null ? 'no data ever' : `no data for ${Math.round(days)}d`;
        case 'failing':
            return `failing, no data for ${days === null ? '?' : Math.round(days)}d`;
        case 'gap-days':
            return `${stream.profile.unexpectedNullDays} empty days`;
        case 'not-running':
            return 'not running';
        case 'less-than-normal':
            return 'less data than normal';
        case 'more-than-normal':
            return 'more data than normal';
        default:
            return stream.category;
    }
}

function streamIcon(status: DataStream['status']): string {
    switch (status) {
        case 'stalled':
            return 'circle-slash';
        case 'degraded':
            return 'warning';
        case 'watch':
            return 'eye';
        case 'healthy':
            return 'pass';
        default:
            return 'question';
    }
}

/** Severity chooses the colour, status chooses the icon: a stopped stream with one detector behind it still
 * reads as stopped, but it is not painted critical until a second independent test agrees. */
function streamColor(severity: DataStream['severity']): vscode.ThemeColor | undefined {
    switch (severity) {
        case 'critical':
            return new vscode.ThemeColor('charts.red');
        case 'warning':
            return new vscode.ThemeColor('charts.yellow');
        default:
            return undefined;
    }
}

function errorNode(err: unknown): vscode.TreeItem {
    if (err instanceof AuthError) {
        return messageNode('Sign in to view…', { command: 'sqlflow.signIn', title: 'Sign In' });
    }
    const item = messageNode(err instanceof Error ? err.message : String(err));
    item.iconPath = new vscode.ThemeIcon('error');
    return item;
}

function statusIcon(status: string): string {
    switch (status) {
        case 'succeeded':
            return 'pass';
        case 'failed':
            return 'error';
        case 'running':
            return 'sync~spin';
        case 'queued':
            return 'clock';
        case 'cancelled':
            return 'circle-slash';
        default:
            return 'circle-outline';
    }
}
