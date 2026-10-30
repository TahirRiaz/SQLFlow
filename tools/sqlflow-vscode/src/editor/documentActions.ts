import * as path from 'path';
import * as vscode from 'vscode';
import { DocumentIndex, IndexedDocument } from '../documents/documentIndex';
import { DocumentProblem, Estate } from '../mcp/estate';
import { ago } from '../views/trees';

/** A skeleton `New Document` offers: a document of one kind, with the keys it cannot do without. */
export interface DocumentSkeleton {
    label: string;
    detail: string;
    content: string;
    /** The file name it suggests, when the kind has a convention. */
    fileName?: string;
}

/** Starts a document from a skeleton of the kind asked for. */
export async function newDocument(skeletons: DocumentSkeleton[]): Promise<void> {
    const chosen = await vscode.window.showQuickPick(skeletons.map((skeleton) => ({ ...skeleton, description: undefined })), {
        placeHolder: 'Which kind of document?',
        matchOnDetail: true,
    });
    if (!chosen) {
        return;
    }
    const document = await vscode.workspace.openTextDocument({ language: 'sqlflow-flow', content: chosen.content });
    await vscode.window.showTextDocument(document);
}

function severityOf(problem: DocumentProblem): vscode.DiagnosticSeverity {
    switch (problem.severity) {
        case 'error':
            return vscode.DiagnosticSeverity.Error;
        case 'warning':
            return vscode.DiagnosticSeverity.Warning;
        case 'info':
            return vscode.DiagnosticSeverity.Information;
        default:
            return vscode.DiagnosticSeverity.Hint;
    }
}

/**
 * Checks every document of the workspace with the engine the editor uses, so a problem shows in the Problems panel for a
 * file nobody has open. An open file already has the language server's answer, so it is left to that.
 */
export class RepositoryValidation implements vscode.Disposable {
    private readonly diagnostics = vscode.languages.createDiagnosticCollection('sqlflow-repository');

    constructor(private readonly documents: DocumentIndex, private readonly estate: Estate, private readonly product: string) {}

    dispose(): void {
        this.diagnostics.dispose();
    }

    async run(): Promise<void> {
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `${this.product}: validating the repository`, cancellable: true },
            async (progress, cancel) => {
                this.diagnostics.clear();
                const documents = await this.documents.all();
                const open = new Set(vscode.workspace.textDocuments.map((document) => document.uri.toString()));
                let errors = 0;
                let warnings = 0;
                for (const [index, document] of documents.entries()) {
                    if (cancel.isCancellationRequested) {
                        return;
                    }
                    progress.report({ message: `${index + 1} of ${documents.length}`, increment: 100 / documents.length });
                    if (open.has(document.uri.toString())) {
                        continue;
                    }
                    const found = await this.check(document);
                    errors += found.filter((problem) => problem.severity === vscode.DiagnosticSeverity.Error).length;
                    warnings += found.filter((problem) => problem.severity === vscode.DiagnosticSeverity.Warning).length;
                }
                const summary = `${documents.length} documents: ${errors} errors, ${warnings} warnings.`;
                if (errors + warnings > 0) {
                    void vscode.commands.executeCommand('workbench.actions.view.problems');
                    void vscode.window.showWarningMessage(`${this.product}: ${summary}`);
                } else {
                    void vscode.window.showInformationMessage(`${this.product}: ${summary}`);
                }
            }
        );
    }

    private async check(document: IndexedDocument): Promise<vscode.Diagnostic[]> {
        const text = Buffer.from(await vscode.workspace.fs.readFile(document.uri)).toString('utf8');
        const problems = await this.estate.validate(text);
        const diagnostics = problems.map((problem) => {
            const line = Math.max(0, problem.line - 1);
            const range = new vscode.Range(line, Math.max(0, problem.column - 1), line, Number.MAX_SAFE_INTEGER);
            const diagnostic = new vscode.Diagnostic(range, problem.message, severityOf(problem));
            diagnostic.source = this.product;
            diagnostic.code = problem.code ?? undefined;
            return diagnostic;
        });
        this.diagnostics.set(document.uri, diagnostics);
        return diagnostics;
    }
}

/** A lens of a module's: the action at the top of a document it knows. */
export type LensProvider = (document: IndexedDocument, text: vscode.TextDocument) => Promise<vscode.Command[]>;

/** The line a lens sits on: the first key, past the comment most documents open with. */
function firstKeyLine(document: vscode.TextDocument): number {
    for (let line = 0; line < document.lineCount; line++) {
        const text = document.lineAt(line).text;
        if (text.trim() !== '' && !text.trimStart().startsWith('#')) {
            return line;
        }
    }
    return 0;
}

/** The actions at the top of a document: run it, see how it last ran, what differs from what runs. */
export class DocumentLenses implements vscode.CodeLensProvider {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeCodeLenses = this.emitter.event;
    private readonly providers: LensProvider[] = [];

    constructor(private readonly documents: DocumentIndex, private readonly estate: Estate) {}

    register(provider: LensProvider): void {
        this.providers.push(provider);
    }

    refresh(): void {
        this.emitter.fire();
    }

    async provideCodeLenses(text: vscode.TextDocument): Promise<vscode.CodeLens[]> {
        const document = await this.documents.at(text.uri);
        if (!document) {
            return [];
        }
        const commands = [...(await this.flowCommands(document))];
        for (const provider of this.providers) {
            commands.push(...(await provider(document, text).catch(() => [])));
        }
        const line = firstKeyLine(text);
        return commands.map((command) => new vscode.CodeLens(new vscode.Range(line, 0, line, 0), command));
    }

    private async flowCommands(document: IndexedDocument): Promise<vscode.Command[]> {
        if (document.kind !== 'flow') {
            return [];
        }
        const commands: vscode.Command[] = [{ title: '$(play) Run', command: 'sqlflow.runFlow', arguments: [document.uri] }];
        if (!document.name) {
            return commands;
        }
        // The estate is a bonus on a lens: signed out or unreachable, the document still has its Run.
        try {
            const pipeline = await this.estate.pipelineNamed(document.name, document.relativePath);
            if (!pipeline) {
                commands.push({ title: 'Not synced', command: 'sqlflow.refreshCatalog' });
                return commands;
            }
            const [run] = await this.estate.runs({ pipelineId: pipeline.id, pageSize: 1 });
            if (run) {
                commands.push({
                    title: `Last run: ${run.status} ${ago(run.endUtc ?? run.startUtc ?? run.writtenUtc)}`,
                    command: 'sqlflow.openRun',
                    arguments: [run.runId],
                });
            }
            commands.push({ title: 'Compare with Synced', command: 'sqlflow.compareWithSynced', arguments: [document.uri] });
        } catch {
            return commands;
        }
        return commands;
    }
}

/** The scheme of the read-only copy of what the estate runs, shown against the local file. */
export const SYNCED_SCHEME = 'sqlflow-synced';

/** Serves the synced copy of a flow to a diff. */
export class SyncedDefinitions implements vscode.TextDocumentContentProvider {
    constructor(private readonly estate: Estate) {}

    async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
        return (await this.estate.pipeline(uri.path.replace(/^\//, ''))).yaml;
    }
}

/** Shows what a local file changes from what the estate runs. */
export async function compareWithSynced(estate: Estate, documents: DocumentIndex, uri: vscode.Uri): Promise<void> {
    const document = await documents.at(uri);
    const pipeline = document?.name ? await estate.pipelineNamed(document.name, document.relativePath) : undefined;
    if (!pipeline) {
        void vscode.window.showInformationMessage(`${path.basename(uri.fsPath)} is not in the estate yet.`);
        return;
    }
    await vscode.commands.executeCommand(
        'vscode.diff',
        vscode.Uri.from({ scheme: SYNCED_SCHEME, path: `/${pipeline.id}` }),
        uri,
        `${pipeline.name}: synced ↔ this file`
    );
}

/** A command line verb a task can run on a document. */
export interface CliVerb {
    verb: string;
    label: string;
    detail: string;
    /** Whether it applies to a document. */
    applies(document: IndexedDocument): boolean;
}

/** The task type `tasks.json` names. */
export const TASK_TYPE = 'sqlflow';

interface CliTaskDefinition extends vscode.TaskDefinition {
    verb: string;
    file: string;
}

/** A verb on a file as a task: the CLI reads the working copy on this machine, so it answers for what is not yet pushed. */
function cliTask(definition: CliTaskDefinition, scope: vscode.WorkspaceFolder | vscode.TaskScope): vscode.Task {
    const cli = vscode.workspace.getConfiguration('sqlflow').get<string>('cliPath', 'sqlflow').trim() || 'sqlflow';
    const execution = new vscode.ShellExecution(cli, [definition.verb, { value: definition.file, quoting: vscode.ShellQuoting.Strong }]);
    const task = new vscode.Task(definition, scope, `${definition.verb} ${path.basename(definition.file)}`, TASK_TYPE, execution);
    task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true };
    return task;
}

/** Offers a document's verbs and runs the one picked. */
export async function runCliVerb(verbs: CliVerb[], documents: DocumentIndex): Promise<void> {
    const uri = vscode.window.activeTextEditor?.document.uri;
    const document = uri ? await documents.at(uri) : undefined;
    if (!uri || !document) {
        void vscode.window.showInformationMessage('Open a pipeline document first.');
        return;
    }
    const chosen = await vscode.window.showQuickPick(
        verbs.filter((verb) => verb.applies(document)),
        { placeHolder: `Run on ${path.basename(uri.fsPath)}, on this machine` }
    );
    if (chosen) {
        const scope = vscode.workspace.getWorkspaceFolder(uri) ?? vscode.TaskScope.Workspace;
        await vscode.tasks.executeTask(cliTask({ type: TASK_TYPE, verb: chosen.verb, file: uri.fsPath }, scope));
    }
}

/** Resolves a task `tasks.json` declares, so a verb can be bound to a key or a build. */
export function cliTaskProvider(): vscode.TaskProvider {
    return {
        provideTasks: () => [],
        resolveTask: (task) => {
            const definition = task.definition as CliTaskDefinition;
            return definition.verb && definition.file ? cliTask(definition, task.scope ?? vscode.TaskScope.Workspace) : undefined;
        },
    };
}
