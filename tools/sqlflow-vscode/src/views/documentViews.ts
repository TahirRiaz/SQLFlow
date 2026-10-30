import * as path from 'path';
import * as vscode from 'vscode';
import { DocumentIndex, IndexedDocument } from '../documents/documentIndex';
import { kindLabel } from '../documents/recognition';
import { Estate, Pipeline } from '../mcp/estate';
import { ago, errorNode, messageNode, statusIcon } from './trees';

/** A row of the Document view: what it says, and what opening it does. */
export interface InfoRow {
    label: string;
    description?: string;
    tooltip?: string;
    icon?: string;
    command?: vscode.Command;
    /** The rows under this one, read when it is opened. */
    children?: () => Promise<InfoRow[]>;
}

/** What a section knows about the document it describes. */
export interface DocumentContext {
    document: IndexedDocument;
    /** The catalog's flow of this document, when the estate has one. */
    pipeline?: Pipeline;
    text: string;
}

/**
 * A part of the Document view. SQLFlow's own sections say what any flow is; a module adds what its kinds are. A section
 * reads when it is opened, so moving between files asks the estate for nothing a person does not look at.
 */
export interface DocumentSection {
    label: string;
    icon: string;
    /** Whether it says anything about this document. */
    applies(context: DocumentContext): boolean;
    /** Open as the document opens: for what a person always wants to see. */
    expanded?: boolean;
    rows(context: DocumentContext): Promise<InfoRow[]>;
}

class RowNode extends vscode.TreeItem {
    constructor(readonly row: InfoRow, state?: vscode.TreeItemCollapsibleState) {
        super(row.label, state ?? (row.children ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None));
        this.description = row.description;
        this.tooltip = row.tooltip;
        this.iconPath = row.icon ? new vscode.ThemeIcon(row.icon) : undefined;
        this.command = row.command;
    }
}

/** A view of rows a module reads: rows open as they are asked for, and a row it cannot read says why in its place. */
export class InfoTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly roots: () => Promise<InfoRow[]>) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        try {
            const rows = element instanceof RowNode ? (await element.row.children?.()) ?? [] : await this.roots();
            return rows.length === 0 ? [messageNode('Nothing to show.')] : rows.map((row) => new RowNode(row));
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

/**
 * The view that follows the editor: everything known about the document in front of the person, from the language and
 * the repository first, and from the estate when there is one. It is where a person sees what a flow reads, what it
 * feeds, how it last ran and whether what they are editing is what runs.
 */
export class DocumentViewProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;
    private readonly sections: DocumentSection[] = [];

    constructor(private readonly estate: Estate, private readonly documents: DocumentIndex) {}

    register(section: DocumentSection): void {
        this.sections.push(section);
    }

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        try {
            if (element instanceof RowNode) {
                return ((await element.row.children?.()) ?? []).map((row) => new RowNode(row));
            }
            if (element) {
                return [];
            }
            const context = await this.currentContext();
            if (!context) {
                return [messageNode('Open a flow, a mapping or another pipeline document.')];
            }
            return this.sections
                .filter((section) => section.applies(context))
                .map((section) => new RowNode(
                    { label: section.label, icon: section.icon, children: () => section.rows(context) },
                    section.expanded ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed
                ));
        } catch (err) {
            return [errorNode(err)];
        }
    }

    private async currentContext(): Promise<DocumentContext | undefined> {
        const editor = vscode.window.activeTextEditor;
        if (!editor) {
            return undefined;
        }
        const document = await this.documents.at(editor.document.uri);
        if (!document) {
            return undefined;
        }
        return { document, text: editor.document.getText(), pipeline: await this.pipelineOf(document) };
    }

    /** The catalog's flow of a document. Reading the catalog needs a sign-in; a document is described without one. */
    private async pipelineOf(document: IndexedDocument): Promise<Pipeline | undefined> {
        if (document.kind !== 'flow' || !document.name) {
            return undefined;
        }
        try {
            return await this.estate.pipelineNamed(document.name, document.relativePath);
        } catch {
            return undefined;
        }
    }
}

/** SQLFlow's own sections: what any document is, and what the estate knows of any flow. */
export function builtInSections(estate: Estate): DocumentSection[] {
    return [
        {
            label: 'Document',
            icon: 'file-code',
            expanded: true,
            applies: () => true,
            rows: async ({ document, pipeline }) => [
                { label: 'Kind', description: kindLabel(document), icon: 'symbol-class' },
                ...(document.name ? [{ label: 'Name', description: document.name, icon: 'tag' }] : []),
                pipeline
                    ? { label: 'In the estate', description: [pipeline.active === false ? 'inactive' : 'active', pipeline.batch].filter(Boolean).join(' · '), icon: 'cloud' }
                    : { label: 'In the estate', description: document.kind === 'flow' ? 'not synced' : 'not a flow', icon: 'circle-slash' },
                ...(pipeline ? [await matchesSynced(estate, pipeline, document)] : []),
            ],
        },
        {
            label: 'Latest Runs',
            icon: 'history',
            expanded: true,
            applies: ({ pipeline }) => pipeline !== undefined,
            rows: async ({ pipeline }) => {
                const runs = await estate.runs({ pipelineId: pipeline?.id, pageSize: 5 });
                return runs.length === 0
                    ? [{ label: 'No runs yet.' }]
                    : runs.map((run) => ({
                        label: run.status,
                        description: [run.operation, ago(run.endUtc ?? run.startUtc ?? run.writtenUtc)].filter(Boolean).join(' · '),
                        tooltip: run.error ?? undefined,
                        icon: statusIcon(run.status),
                        command: { command: 'sqlflow.openRun', title: 'Open', arguments: [run.runId] },
                    }));
            },
        },
        {
            label: 'Runs After and Before',
            icon: 'type-hierarchy',
            applies: ({ pipeline }) => pipeline !== undefined,
            rows: async ({ pipeline }) => {
                if (!pipeline) {
                    return [];
                }
                const dependencies = await estate.dependencies(pipeline.repoId, pipeline.id);
                const rows = dependencies.map((dependency) => {
                    const waits = dependency.toFlow === pipeline.name;
                    return {
                        label: (waits ? dependency.fromFlow : dependency.toFlow) ?? '',
                        description: `${waits ? 'runs first' : 'runs after'} · ${dependency.viaObjects ?? ''}`,
                        icon: waits ? 'arrow-up' : 'arrow-down',
                        command: { command: 'sqlflow.openFlowByName', title: 'Open', arguments: [waits ? dependency.fromFlow : dependency.toFlow] },
                    };
                });
                return rows.length === 0 ? [{ label: 'Nothing in this repository depends on it.' }] : rows;
            },
        },
        {
            label: 'Columns',
            icon: 'symbol-field',
            applies: ({ pipeline }) => pipeline !== undefined,
            rows: async ({ pipeline }) => {
                const columns = await estate.pipelineColumns(pipeline?.id ?? '');
                return columns.length === 0
                    ? [{ label: 'The catalog holds no columns for this flow yet.' }]
                    : columns.map((column) => ({ label: column.columnName, description: column.dataType ?? undefined, icon: 'symbol-field' }));
            },
        },
    ];
}

/** Whether the file open here is what the estate runs. */
async function matchesSynced(estate: Estate, pipeline: Pipeline, document: IndexedDocument): Promise<InfoRow> {
    const synced = await estate.pipeline(pipeline.id);
    const local = await vscode.workspace.openTextDocument(document.uri);
    const same = normalise(local.getText()) === normalise(synced.yaml);
    return same
        ? { label: 'This file', description: 'is what the estate runs', icon: 'pass' }
        : {
            label: 'This file',
            description: 'differs from what the estate runs',
            icon: 'diff',
            command: { command: 'sqlflow.compareWithSynced', title: 'Compare', arguments: [document.uri] },
        };
}

function normalise(text: string): string {
    return text.replace(/\r\n/g, '\n').trim();
}

class GroupNode extends vscode.TreeItem {
    constructor(label: string, readonly members: IndexedDocument[]) {
        super(label, vscode.TreeItemCollapsibleState.Collapsed);
        this.description = String(members.length);
        this.iconPath = new vscode.ThemeIcon('folder');
    }
}

class DocumentNode extends vscode.TreeItem {
    constructor(document: IndexedDocument, pipeline: Pipeline | undefined) {
        super(document.name ?? path.basename(document.uri.fsPath), vscode.TreeItemCollapsibleState.None);
        this.description = document.relativePath;
        this.resourceUri = document.uri;
        this.iconPath = new vscode.ThemeIcon(pipeline ? 'cloud' : 'file-code');
        this.tooltip = pipeline ? 'In the estate' : undefined;
        this.contextValue = 'document';
        this.command = { command: 'vscode.open', title: 'Open', arguments: [document.uri] };
    }
}

/** The word a group of a repository's documents goes by. */
function groupOf(document: IndexedDocument): string {
    return document.kind === 'flow' ? `${kindLabel(document)} flows` : `${kindLabel(document)} documents`;
}

/** Every pipeline document the workspace holds, by kind: the repository as a developer works in it, before any sync. */
export class RepositoryViewProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly documents: DocumentIndex, private readonly estate: Estate) {
        documents.changed(() => this.refresh());
    }

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        if (element instanceof GroupNode) {
            const synced = await this.syncedNames();
            return element.members.map((document) => new DocumentNode(document, document.name ? synced.get(document.name) : undefined));
        }
        if (element) {
            return [];
        }

        const documents = await this.documents.all();
        if (documents.length === 0) {
            return [messageNode('No pipeline documents in this workspace.')];
        }
        const groups = new Map<string, IndexedDocument[]>();
        for (const document of documents) {
            const group = groupOf(document);
            groups.set(group, [...(groups.get(group) ?? []), document]);
        }
        return [...groups.entries()]
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([label, members]) => new GroupNode(label, members));
    }

    /** The catalog's flows by name. A repository is listed without them, since it reads from disk. */
    private async syncedNames(): Promise<Map<string, Pipeline>> {
        try {
            return new Map((await this.estate.pipelines({})).map((pipeline) => [pipeline.name, pipeline]));
        } catch {
            return new Map();
        }
    }
}
