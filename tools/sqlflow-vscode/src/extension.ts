import * as vscode from 'vscode';
import { startLspClient, stopLspClient } from './lsp/lspClient';
import { Session, errText } from './api/session';
import { AuthError, ControlPlaneClient } from './api/controlPlaneClient';
import { CatalogTreeProvider, DataStreamNode, DataStreamsTreeProvider, PipelineNode, RepoNode, RunNode, RunsTreeProvider, ScheduleNode, SchedulesTreeProvider } from './views/trees';
import { DataStreamPanel } from './views/dataStreamPanel';
import { Docs } from './docs/docs';
import { setupMcp } from './mcp/mcpSetup';

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const output = vscode.window.createOutputChannel('SQLFlow');
    context.subscriptions.push(output);

    const session = new Session(context.secrets);
    const client = new ControlPlaneClient(session);
    const docs = new Docs(context.extensionPath);

    const catalog = new CatalogTreeProvider(client);
    const runs = new RunsTreeProvider(client);
    const schedules = new SchedulesTreeProvider(client);
    const dataStreams = new DataStreamsTreeProvider(client);
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('sqlflowCatalog', catalog),
        vscode.window.registerTreeDataProvider('sqlflowRuns', runs),
        vscode.window.registerTreeDataProvider('sqlflowSchedules', schedules),
        vscode.window.registerTreeDataProvider('sqlflowDataStreams', dataStreams)
    );

    const refreshAll = () => {
        catalog.refresh();
        runs.refresh();
        schedules.refresh();
        dataStreams.refresh();
    };
    context.subscriptions.push(session.changed(refreshAll));

    // Start the language server (optional; catalog features work without it).
    void startLspClient(context, output);

    const register = (id: string, fn: (...args: unknown[]) => unknown) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, fn));

    register('sqlflow.signIn', async () => {
        if (await session.signIn(client.baseUrl())) {
            refreshAll();
        }
    });

    register('sqlflow.signOut', async () => {
        await session.clear();
        void vscode.window.showInformationMessage('SQLFlow: signed out.');
    });

    register('sqlflow.setControlPlaneUrl', async () => {
        const url = await vscode.window.showInputBox({
            prompt: 'SQLFlow control plane base URL',
            value: client.baseUrl(),
            ignoreFocusOut: true,
        });
        if (url) {
            await vscode.workspace.getConfiguration('sqlflow').update('controlPlaneUrl', url.trim(), vscode.ConfigurationTarget.Workspace);
            refreshAll();
        }
    });

    register('sqlflow.newFlow', async () => {
        const doc = await vscode.workspace.openTextDocument({
            language: 'sqlflow-flow',
            content: 'name: newFlow\nsource:\n  type: csv\n  path: ./data/*.csv\ntarget:\n  server: dwh\n  table: stg.newFlow\n',
        });
        await vscode.window.showTextDocument(doc);
    });

    register('sqlflow.validateFlow', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.languageId !== 'sqlflow-flow') {
            void vscode.window.showInformationMessage('Open a .flow.yaml file to validate.');
            return;
        }
        const diags = vscode.languages.getDiagnostics(editor.document.uri);
        const errors = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Error).length;
        const warnings = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Warning).length;
        if (diags.length === 0) {
            void vscode.window.showInformationMessage('SQLFlow: no problems found.');
        } else {
            void vscode.commands.executeCommand('workbench.actions.view.problems');
            void vscode.window.showWarningMessage(`SQLFlow: ${errors} error(s), ${warnings} warning(s).`);
        }
    });

    register('sqlflow.searchDocs', () => docs.search());

    register('sqlflow.openDocsForKey', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor) {
            return;
        }
        const path = pathAtCursor(editor.document, editor.selection.active);
        if (!path) {
            void vscode.window.showInformationMessage('No flow key at the cursor.');
            return;
        }
        if (!(await docs.openForYamlPath(path))) {
            void vscode.window.showInformationMessage(`No reference page documents '${path}'.`);
        }
    });

    register('sqlflow.refreshCatalog', refreshAll);
    register('sqlflow.refreshRuns', () => runs.refresh());

    register('sqlflow.refreshDataStreams', () => dataStreams.refresh());

    register('sqlflow.runFlow', async (arg: unknown) => {
        await withAuth(session, client, output, async () => {
            const pipeline = await resolvePipeline(client, arg);
            if (!pipeline) {
                return;
            }
            if (!pipeline.repoId) {
                void vscode.window.showErrorMessage('This flow has no known repository; run it from the Catalog view.');
                return;
            }
            const full = await vscode.window.showQuickPick(['Incremental', 'Full load'], { placeHolder: `Run '${pipeline.name}'` });
            if (!full) {
                return;
            }
            const accepted = await client.triggerRun({
                repoId: pipeline.repoId,
                flowName: pipeline.name,
                fullLoad: full === 'Full load',
            });
            void vscode.window.showInformationMessage(`SQLFlow: queued run ${accepted.runId} (${accepted.status}).`);
            runs.refresh();
        });
    });

    register('sqlflow.openPipeline', async (arg: unknown) => {
        if (!(arg instanceof PipelineNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            const def = await client.pipelineDefinition(arg.pipeline.id);
            await openJson(`${arg.pipeline.name}.definition.json`, def);
        });
    });

    register('sqlflow.openRun', async (arg: unknown) => {
        if (!(arg instanceof RunNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            const run = await client.getRun(arg.run.runId);
            await openJson(`run-${arg.run.runId}.json`, run);
        });
    });

    register('sqlflow.cancelRun', async (arg: unknown) => {
        if (!(arg instanceof RunNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            await client.cancelRun(arg.run.runId);
            void vscode.window.showInformationMessage(`SQLFlow: cancellation requested for run ${arg.run.runId}.`);
            runs.refresh();
        });
    });

    register('sqlflow.runSchedule', async (arg: unknown) => {
        if (!(arg instanceof ScheduleNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            const accepted = await client.runSchedule(arg.schedule.id);
            void vscode.window.showInformationMessage(
                `SQLFlow: schedule fired; queued run ${accepted.runId}.`
            );
            runs.refresh();
            schedules.refresh();
        });
    });

    register('sqlflow.openDataStream', async (arg: unknown) => {
        if (!(arg instanceof DataStreamNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            const days = vscode.workspace.getConfiguration('sqlflow').get<number>('dataStreamWindowDays', 60);
            // The full analysis, including the day-by-day series and every detector's reasoning (the ones
            // that stayed quiet included, so a verdict can be checked rather than taken on trust).
            const detail = await client.getDataStream(arg.stream.pipelineId, days);
            DataStreamPanel.show(detail, days);
        });
    });

    register('sqlflow.showLineage', async (arg: unknown) => {
        if (!(arg instanceof RepoNode)) {
            return;
        }
        await withAuth(session, client, output, async () => {
            const waves = await client.waves(arg.repo.id);
            await openJson(`${arg.repo.name}.waves.json`, waves);
        });
    });

    register('sqlflow.setupMcp', () => setupMcp(context));

    output.appendLine('SQLFlow extension activated.');
}

export async function deactivate(): Promise<void> {
    await stopLspClient();
}

/** Run an action, prompting to sign in (and retrying) on an auth failure. */
async function withAuth(
    session: Session,
    client: ControlPlaneClient,
    output: vscode.OutputChannel,
    action: () => Promise<void>
): Promise<void> {
    try {
        await action();
    } catch (err) {
        if (err instanceof AuthError) {
            const pick = await vscode.window.showWarningMessage(err.message, 'Sign In');
            if (pick === 'Sign In' && (await session.signIn(client.baseUrl()))) {
                try {
                    await action();
                } catch (retryErr) {
                    output.appendLine(`[error] ${errText(retryErr)}`);
                    void vscode.window.showErrorMessage(`SQLFlow: ${errText(retryErr)}`);
                }
            }
            return;
        }
        output.appendLine(`[error] ${errText(err)}`);
        void vscode.window.showErrorMessage(`SQLFlow: ${errText(err)}`);
    }
}

/** Resolve a pipeline from a tree node, or let the user pick one. */
async function resolvePipeline(
    client: ControlPlaneClient,
    arg: unknown
): Promise<{ id: string; name: string; repoId?: string } | undefined> {
    if (arg instanceof PipelineNode) {
        return arg.pipeline as { id: string; name: string; repoId?: string };
    }
    const result = await client.listPipelines({ active: true });
    const pipelines = Array.isArray(result) ? result : result.items;
    const chosen = await vscode.window.showQuickPick(
        pipelines.map((p) => ({ label: p.name, description: p.kind, id: p.id, repoId: p.repoId, name: p.name })),
        { placeHolder: 'Select a flow to run' }
    );
    return chosen ? { id: chosen.id, name: chosen.name, repoId: chosen.repoId } : undefined;
}

async function openJson(name: string, value: unknown): Promise<void> {
    const doc = await vscode.workspace.openTextDocument({
        language: 'json',
        content: JSON.stringify(value, null, 2),
    });
    await vscode.window.showTextDocument(doc, { preview: true });
    void name;
}

/** Reconstruct the dotted key path at the cursor from YAML indentation. */
function pathAtCursor(document: vscode.TextDocument, position: vscode.Position): string | undefined {
    const keyOf = (line: string): { indent: number; key: string } | undefined => {
        const indent = line.length - line.trimStart().length;
        const content = line.slice(indent).replace(/^-\s+/, '');
        const m = content.match(/^["']?([\w.$-]+)["']?\s*:/);
        return m ? { indent, key: m[1] } : undefined;
    };

    const here = keyOf(document.lineAt(position.line).text);
    if (!here) {
        return undefined;
    }
    const segments = [here.key];
    let targetIndent = here.indent;
    for (let l = position.line - 1; l >= 0; l--) {
        const text = document.lineAt(l).text;
        if (text.trim() === '' || text.trimStart().startsWith('#')) {
            continue;
        }
        const parsed = keyOf(text);
        if (parsed && parsed.indent < targetIndent) {
            segments.unshift(parsed.key);
            targetIndent = parsed.indent;
            if (targetIndent === 0) {
                break;
            }
        }
    }
    return segments.join('.');
}
