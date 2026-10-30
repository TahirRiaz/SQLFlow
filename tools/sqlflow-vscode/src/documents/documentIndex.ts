import * as vscode from 'vscode';
import { RecognisedDocument, recogniseDocument } from './recognition';

/** The folders a scan leaves out: what a build, a tool or a run writes, which holds no document of the repository. */
const EXCLUDE = '**/{node_modules,.git,.sqlflow,.work,dist,bin,obj,target,.venv}/**';

/** A repository larger than this is read in part, and says so in the log. */
const MAX_DOCUMENTS = 5000;

/** Far enough into a file for its root keys: a document's keys sit at its top, and a data file can be any size. */
const READ_BYTES = 256 * 1024;

export interface IndexedDocument extends RecognisedDocument {
    uri: vscode.Uri;
    /** The path from the workspace folder, forward slashed. */
    relativePath: string;
}

/**
 * Every pipeline document the workspace holds, recognised by content and kept current as files change. What a repository
 * holds is known here without the control plane, so the views, the completions and the links work before anything is
 * pushed, which is when a developer needs them.
 */
export class DocumentIndex implements vscode.Disposable {
    private readonly documents = new Map<string, IndexedDocument>();
    private readonly onChange = new vscode.EventEmitter<void>();
    readonly changed = this.onChange.event;
    private readonly watcher = vscode.workspace.createFileSystemWatcher('**/*.yaml');
    private scan: Promise<void> | undefined;
    private readonly output: vscode.LogOutputChannel;

    constructor(output: vscode.LogOutputChannel) {
        this.output = output;
        this.watcher.onDidCreate((uri) => void this.read(uri).then(() => this.onChange.fire()));
        this.watcher.onDidChange((uri) => void this.read(uri).then(() => this.onChange.fire()));
        this.watcher.onDidDelete((uri) => {
            if (this.documents.delete(uri.toString())) {
                this.onChange.fire();
            }
        });
    }

    dispose(): void {
        this.watcher.dispose();
        this.onChange.dispose();
    }

    /** Every document, in path order, once the workspace has been read. */
    async all(): Promise<IndexedDocument[]> {
        await (this.scan ??= this.readWorkspace());
        return [...this.documents.values()].sort((a, b) => a.relativePath.localeCompare(b.relativePath));
    }

    /** Reads the workspace again. */
    async refresh(): Promise<void> {
        this.documents.clear();
        this.scan = this.readWorkspace();
        await this.scan;
        this.onChange.fire();
    }

    /** The document of a uri, read now when the scan has not reached it. */
    async at(uri: vscode.Uri): Promise<IndexedDocument | undefined> {
        return this.documents.get(uri.toString()) ?? (await this.read(uri));
    }

    private async readWorkspace(): Promise<void> {
        const files = await vscode.workspace.findFiles('**/*.yaml', EXCLUDE, MAX_DOCUMENTS + 1);
        if (files.length > MAX_DOCUMENTS) {
            this.output.warn(`[documents] the workspace holds more than ${MAX_DOCUMENTS} YAML files; the first ${MAX_DOCUMENTS} are read.`);
        }
        await Promise.all(files.slice(0, MAX_DOCUMENTS).map((uri) => this.read(uri)));
    }

    private async read(uri: vscode.Uri): Promise<IndexedDocument | undefined> {
        if (EXCLUDED_SEGMENT.test(uri.path)) {
            return undefined;
        }
        try {
            const bytes = await vscode.workspace.fs.readFile(uri);
            const text = Buffer.from(bytes.subarray(0, READ_BYTES)).toString('utf8');
            const recognised = recogniseDocument(uri.path, text);
            if (!recognised) {
                this.documents.delete(uri.toString());
                return undefined;
            }
            const document = {
                ...recognised,
                uri,
                relativePath: vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/'),
            };
            this.documents.set(uri.toString(), document);
            return document;
        } catch (err) {
            this.output.debug(`[documents] ${uri.fsPath}: ${err}`);
            this.documents.delete(uri.toString());
            return undefined;
        }
    }
}

const EXCLUDED_SEGMENT = /\/(node_modules|\.git|\.sqlflow|\.work|dist|bin|obj|target|\.venv)\//;

/**
 * Puts a YAML file that is a pipeline document under this extension's language as it opens, so the language server, the
 * lenses and the links reach it. The file's name says nothing here, since a repository's flows are plain `*.yaml`.
 */
export function recogniseOpenedDocuments(): vscode.Disposable {
    const assign = (document: vscode.TextDocument): void => {
        const enabled = vscode.workspace.getConfiguration('sqlflow').get<boolean>('recognizeDocuments', true);
        if (enabled && document.languageId === 'yaml' && document.uri.scheme === 'file'
            && recogniseDocument(document.uri.path, document.getText()) !== undefined) {
            void vscode.languages.setTextDocumentLanguage(document, 'sqlflow-flow');
        }
    };
    vscode.workspace.textDocuments.forEach(assign);
    return vscode.workspace.onDidOpenTextDocument(assign);
}
