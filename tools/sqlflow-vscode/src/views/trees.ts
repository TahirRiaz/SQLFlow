import * as vscode from 'vscode';
import { DataStream, Estate, Pipeline, Repo, RepoSource, Run, Schedule } from '../mcp/estate';
import { SignInNeeded } from '../mcp/toolAnswer';

/** A leaf node shown when a view has nothing to list, or cannot read it (e.g. not signed in). */
export function messageNode(label: string, command?: vscode.Command): vscode.TreeItem {
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.command = command;
    item.iconPath = new vscode.ThemeIcon('info');
    return item;
}

/** What a view shows in place of what it could not read. */
export function errorNode(err: unknown): vscode.TreeItem {
    if (err instanceof SignInNeeded) {
        return messageNode('Sign in to view…', { command: 'sqlflow.signIn', title: 'Sign In' });
    }
    const item = messageNode(err instanceof Error ? err.message : String(err));
    item.iconPath = new vscode.ThemeIcon('error');
    return item;
}

/** How long ago an instant was, the way a person says it. */
export function ago(utc: string | null | undefined): string {
    if (!utc) {
        return '';
    }
    const seconds = Math.round((Date.now() - Date.parse(utc)) / 1000);
    if (Number.isNaN(seconds)) {
        return '';
    }
    if (seconds < 90) {
        return 'just now';
    }
    const minutes = Math.round(seconds / 60);
    if (minutes < 90) {
        return `${minutes}m ago`;
    }
    const hours = Math.round(minutes / 60);
    return hours < 36 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`;
}

export class RepoNode extends vscode.TreeItem {
    constructor(public readonly repo: Repo, public readonly source: RepoSource | undefined) {
        super(repo.name ?? repo.id, vscode.TreeItemCollapsibleState.Collapsed);
        this.contextValue = source ? 'repo-synced' : 'repo';
        this.iconPath = new vscode.ThemeIcon(source?.lastError ? 'warning' : source?.syncPending ? 'sync~spin' : 'repo');
        this.id = `repo:${repo.id}`;
        const commit = source?.lastSyncedSha?.slice(0, 8);
        this.description = [commit, source?.branch, ago(source?.lastSyncUtc ?? repo.lastSyncUtc)].filter(Boolean).join(' · ');
        this.tooltip = source?.lastError ? `The last sync failed: ${source.lastError}` : undefined;
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

    constructor(private readonly estate: Estate) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        try {
            if (!element) {
                const [repos, sources] = await Promise.all([this.estate.repos(), this.estate.repoSources()]);
                return repos.length
                    ? repos.map((r) => new RepoNode(r, sources.find((s) => s.name === r.name || (s.remoteUrl && s.remoteUrl === r.remoteUrl))))
                    : [messageNode('No repositories synced yet.')];
            }
            if (element instanceof RepoNode) {
                const pipelines = await this.estate.pipelines({ repoId: element.repo.id });
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
        const active = isActive(run);
        this.contextValue = active ? 'run-active' : 'run';
        this.description = [run.status, run.operation, ago(run.endUtc ?? run.startUtc ?? run.writtenUtc)].filter(Boolean).join(' · ');
        this.iconPath = new vscode.ThemeIcon(statusIcon(run.status));
        this.tooltip = `${run.flowName ?? ''}\nstatus: ${run.status}\nstarted: ${run.startUtc ?? '-'}${run.error ? `\n\n${run.error}` : ''}`;
        this.command = { command: 'sqlflow.openRun', title: 'Open', arguments: [run.runId] };
    }
}

/** Whether a run is still under way. */
export function isActive(run: Run): boolean {
    return run.status === 'queued' || run.status === 'running';
}

export class RunsTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;
    private timer: NodeJS.Timeout | undefined;

    constructor(private readonly estate: Estate) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        clearTimeout(this.timer);
        try {
            const limit = vscode.workspace.getConfiguration('sqlflow').get<number>('runResultLimit', 50);
            const runs = await this.estate.runs({ pageSize: limit });
            // A list with a run under way keeps itself current until nothing is.
            if (runs.some(isActive)) {
                this.timer = setTimeout(() => this.refresh(), 5000);
            }
            return runs.length ? runs.map((r) => new RunNode(r)) : [messageNode('No runs yet.')];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class SchedulesTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly estate: Estate) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const schedules = await this.estate.schedules();
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
        this.tooltip = `Next: ${schedule.nextFireUtc ?? '-'}\nLast: ${schedule.lastFireUtc ?? '-'}`;
        this.command = { command: 'sqlflow.showSchedulePlan', title: 'Open', arguments: [this] };
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

    constructor(private readonly estate: Estate) {}

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
            const board = await this.estate.dataStreams(days);

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

export function statusIcon(status: string): string {
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
