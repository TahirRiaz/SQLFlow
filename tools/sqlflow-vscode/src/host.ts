import * as path from 'path';
import * as vscode from 'vscode';
import { Docs } from './docs/docs';
import { DocumentIndex, IndexedDocument, recogniseOpenedDocuments } from './documents/documentIndex';
import { keyPathAtLine } from './documents/yamlPath';
import {
    cliTaskProvider,
    CliVerb,
    compareWithSynced,
    DocumentLenses,
    DocumentSkeleton,
    newDocument,
    RepositoryValidation,
    runCliVerb,
    SYNCED_SCHEME,
    SyncedDefinitions,
    TASK_TYPE,
} from './editor/documentActions';
import { builtInReferences, References } from './editor/references';
import { startLspClient, stopLspClient } from './lsp/lspClient';
import { Estate, Pipeline, Run } from './mcp/estate';
import { connectAssistant, registerWithEditor } from './mcp/mcpSetup';
import { McpServer } from './mcp/mcpServer';
import { SignIn } from './mcp/signIn';
import { SignInNeeded } from './mcp/toolAnswer';
import { RunParameter, runFlow, showRun } from './runs/runFlow';
import { DataStreamPanel } from './views/dataStreamPanel';
import { builtInSections, DocumentViewProvider, RepositoryViewProvider } from './views/documentViews';
import { PanelContent, PanelSection } from './views/panelContent';
import { Panels } from './views/panels';
import {
    CatalogTreeProvider,
    DataStreamNode,
    DataStreamsTreeProvider,
    PipelineNode,
    RepoNode,
    RunNode,
    RunsTreeProvider,
    ScheduleNode,
    SchedulesTreeProvider,
} from './views/trees';

const DOCUMENT_SELECTOR: vscode.DocumentSelector = [{ language: 'sqlflow-flow' }];

/**
 * What a product built on SQLFlow gives this extension, as it gives the control plane, the CLI, the GUI and the MCP
 * server: its names, its own server, and modules. The extension here is that composition: SQLFlow's own extension is this
 * host with no modules, and a product's is a package of its own that activates with its host.
 */
export interface VscodeHost {
    /** The product's name, as people read it in notifications and views. */
    product: string;
    /** The MCP server the product composes. */
    mcp: {
        /** Its program. */
        program: string;
        /** Where its sign-in is kept: the state prefix of `<prefix>-token.json`. */
        statePrefix: string;
        /** The id of the manifest's `mcpServerDefinitionProviders`. */
        providerId: string;
        /** Build folders to look in when running from a checkout. */
        searchPaths(extensionPath: string): string[];
    };
    /** Build folders of the language server when running from a checkout. */
    lspSearchPaths(extensionPath: string): string[];
    /** The folders of the key census files its modules add. */
    censusDirectories(extensionPath: string): string[];
    /** The reference corpora it ships, each a folder holding a `manifest.json`. */
    docCorpora(extensionPath: string): string[];
    /** The skeletons `New Document` offers. */
    skeletons: DocumentSkeleton[];
    /** The verbs its command line takes on a document. */
    cliVerbs: CliVerb[];
    modules?: VscodeModule[];
}

/** A module's part of the extension: what it adds when the host activates. */
export interface VscodeModule {
    activate(context: ModuleContext): void;
}

/** What a module adds to, and what it reads through. */
export interface ModuleContext {
    extension: vscode.ExtensionContext;
    product: string;
    estate: Estate;
    documents: DocumentIndex;
    panels: Panels;
    output: vscode.LogOutputChannel;
    /** The Document view: a module adds a section per thing it knows of a document. */
    documentView: DocumentViewProvider;
    /** What keys refer to: a module adds a provider per key it owns. */
    references: References;
    lenses: DocumentLenses;
    /** Registers a command that reads the estate: a failure is shown, and a missing sign-in offers one. */
    command(id: string, action: (...args: unknown[]) => Promise<void>): void;
    /** Everything the views show changed: read it again. */
    refreshed: vscode.Event<void>;
    /** Sections a module adds to a run's panel. */
    addRunSections(sections: (run: Run) => Promise<PanelSection[]>): void;
    /** The parameters a run of a flow takes, when a module knows them. */
    addRunParameters(parameters: (pipeline: Pipeline, document: IndexedDocument | undefined) => Promise<RunParameter[] | undefined>): void;
}

/** Composes the extension for a host. */
export async function activateHost(context: vscode.ExtensionContext, host: VscodeHost): Promise<{ estate: Estate }> {
    // A log channel, since the language client writes its log here too and vscode-languageclient 10 accepts nothing
    // else.
    const output = vscode.window.createOutputChannel(host.product, { log: true });
    context.subscriptions.push(output);

    const { extensionPath } = context;
    const version = String(context.extension.packageJSON.version ?? '0.0.0');
    const mcp = new McpServer({
        program: host.mcp.program,
        searchPaths: host.mcp.searchPaths(extensionPath),
        extensionPath,
        version,
        output,
    });
    const estate = new Estate(mcp);
    const signIn = new SignIn(mcp, host.product, host.mcp.statePrefix);
    const documents = new DocumentIndex(output);
    const panels = new Panels();
    const docs = new Docs(host.docCorpora(extensionPath), mcp);
    context.subscriptions.push(mcp, signIn, documents);

    const catalog = new CatalogTreeProvider(estate);
    const runs = new RunsTreeProvider(estate);
    const schedules = new SchedulesTreeProvider(estate);
    const dataStreams = new DataStreamsTreeProvider(estate);
    const documentView = new DocumentViewProvider(estate, documents);
    const repository = new RepositoryViewProvider(documents, estate);
    builtInSections(estate).forEach((section) => documentView.register(section));
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('sqlflowDocument', documentView),
        vscode.window.registerTreeDataProvider('sqlflowRepository', repository),
        vscode.window.registerTreeDataProvider('sqlflowCatalog', catalog),
        vscode.window.registerTreeDataProvider('sqlflowRuns', runs),
        vscode.window.registerTreeDataProvider('sqlflowSchedules', schedules),
        vscode.window.registerTreeDataProvider('sqlflowDataStreams', dataStreams)
    );

    const onRefresh = new vscode.EventEmitter<void>();
    const onServerChange = new vscode.EventEmitter<void>();
    context.subscriptions.push(onRefresh, onServerChange);
    const references = new References(documents);
    builtInReferences(documents, estate).forEach((provider) => references.register(provider));
    const lenses = new DocumentLenses(documents, estate);
    const validation = new RepositoryValidation(documents, estate, host.product);
    context.subscriptions.push(validation);

    const refreshAll = (): void => {
        estate.forget();
        catalog.refresh();
        runs.refresh();
        schedules.refresh();
        dataStreams.refresh();
        documentView.refresh();
        repository.refresh();
        lenses.refresh();
        onRefresh.fire();
    };
    context.subscriptions.push(
        signIn.changed(refreshAll),
        signIn.changed(() => onServerChange.fire()),
        vscode.window.onDidChangeActiveTextEditor(() => documentView.refresh()),
        vscode.workspace.onDidSaveTextDocument(() => documentView.refresh()),
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('sqlflow.controlPlaneUrl') || event.affectsConfiguration('sqlflow.guiUrl')
                || event.affectsConfiguration('sqlflow.mcpBinaryPath')) {
                mcp.restart();
                onServerChange.fire();
                void signIn.check();
            }
        })
    );

    /** Runs an action that reads the estate: a missing sign-in offers one and tries again, anything else is shown. */
    const guarded = async (action: () => Promise<void>): Promise<void> => {
        try {
            await action();
        } catch (err) {
            if (err instanceof SignInNeeded) {
                const pick = await vscode.window.showWarningMessage(`${host.product}: sign in to do this.`, 'Sign In');
                if (pick === 'Sign In' && (await signIn.signIn())) {
                    await guarded(action);
                }
                return;
            }
            const message = err instanceof Error ? err.message : String(err);
            output.error(message);
            void vscode.window.showErrorMessage(`${host.product}: ${message}`);
        }
    };

    const register = (id: string, fn: (...args: unknown[]) => unknown): void => {
        context.subscriptions.push(vscode.commands.registerCommand(id, fn));
    };
    const command = (id: string, action: (...args: unknown[]) => Promise<void>): void => {
        register(id, (...args: unknown[]) => guarded(() => action(...args)));
    };

    const runSections: ((run: Run) => Promise<PanelSection[]>)[] = [];
    const runParameters: ((pipeline: Pipeline, document: IndexedDocument | undefined) => Promise<RunParameter[] | undefined>)[] = [];

    register('sqlflow.signIn', async () => {
        await signIn.signIn();
    });
    register('sqlflow.signInWithToken', async () => {
        await signIn.signInWithToken();
    });
    command('sqlflow.signOut', () => signIn.signOut());

    register('sqlflow.setControlPlaneUrl', async () => {
        const current = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', '');
        const url = await vscode.window.showInputBox({
            prompt: `${host.product} control plane base URL`,
            value: current,
            ignoreFocusOut: true,
        });
        if (url) {
            await vscode.workspace.getConfiguration('sqlflow').update('controlPlaneUrl', url.trim(), vscode.ConfigurationTarget.Global);
        }
    });

    register('sqlflow.newFlow', () => newDocument(host.skeletons));

    register('sqlflow.validateFlow', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.languageId !== 'sqlflow-flow') {
            void vscode.window.showInformationMessage('Open a pipeline document to validate.');
            return;
        }
        const diags = vscode.languages.getDiagnostics(editor.document.uri);
        const errors = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Error).length;
        const warnings = diags.filter((d) => d.severity === vscode.DiagnosticSeverity.Warning).length;
        if (diags.length === 0) {
            void vscode.window.showInformationMessage(`${host.product}: no problems found.`);
        } else {
            void vscode.commands.executeCommand('workbench.actions.view.problems');
            void vscode.window.showWarningMessage(`${host.product}: ${errors} error(s), ${warnings} warning(s).`);
        }
    });
    command('sqlflow.validateRepository', () => validation.run());
    command('sqlflow.runVerb', () => runCliVerb(host.cliVerbs, documents));

    register('sqlflow.searchDocs', () => docs.search());
    register('sqlflow.openDocsForKey', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor) {
            return;
        }
        const key = keyPathAtLine(editor.document.getText().split(/\r?\n/), editor.selection.active.line);
        if (!key) {
            void vscode.window.showInformationMessage('No key at the cursor.');
            return;
        }
        if (!(await docs.openForYamlPath(key.path))) {
            void vscode.window.showInformationMessage(`No reference page documents '${key.path}'.`);
        }
    });

    register('sqlflow.refreshCatalog', refreshAll);
    register('sqlflow.refreshRuns', () => runs.refresh());
    register('sqlflow.refreshDataStreams', () => dataStreams.refresh());
    register('sqlflow.refreshDocument', () => documentView.refresh());
    command('sqlflow.refreshRepository', () => documents.refresh());

    command('sqlflow.syncRepository', async (arg: unknown) => {
        const source = arg instanceof RepoNode ? arg.source : await pickSource(estate);
        if (!source) {
            return;
        }
        await estate.syncRepoSource(source.id);
        void vscode.window.showInformationMessage(`${host.product}: syncing ${source.name}.`);
        refreshAll();
    });

    const resolvePipeline = async (arg: unknown): Promise<{ pipeline: Pipeline; document?: IndexedDocument } | undefined> => {
        if (arg instanceof PipelineNode) {
            return { pipeline: arg.pipeline };
        }
        const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
        const document = uri ? await documents.at(uri) : undefined;
        if (document?.kind === 'flow' && document.name) {
            const pipeline = await estate.pipelineNamed(document.name, document.relativePath);
            if (pipeline) {
                return { pipeline, document };
            }
            const pick = await vscode.window.showWarningMessage(
                `${host.product}: '${document.name}' is not in the estate. Push it and sync the repository first.`,
                'Sync Repository'
            );
            if (pick === 'Sync Repository') {
                await vscode.commands.executeCommand('sqlflow.syncRepository');
            }
            return undefined;
        }
        const pipelines = await estate.pipelines({ active: true });
        const chosen = await vscode.window.showQuickPick(
            pipelines.map((p) => ({ label: p.name, description: p.kind, pipeline: p })),
            { placeHolder: 'Select a flow' }
        );
        return chosen ? { pipeline: chosen.pipeline } : undefined;
    };

    command('sqlflow.runFlow', async (arg: unknown) => {
        const resolved = await resolvePipeline(arg);
        if (!resolved) {
            return;
        }
        let parameters: RunParameter[] = [];
        for (const reader of runParameters) {
            parameters = (await reader(resolved.pipeline, resolved.document)) ?? parameters;
        }
        await runFlow(estate, host.product, resolved.pipeline, parameters, () => {
            runs.refresh();
            documentView.refresh();
            lenses.refresh();
        });
    });

    command('sqlflow.openPipeline', async (arg: unknown) => {
        if (arg instanceof PipelineNode) {
            await openFlow(estate, documents, arg.pipeline);
        }
    });

    command('sqlflow.openFlowByName', async (arg: unknown) => {
        if (typeof arg === 'string') {
            const pipeline = await estate.pipelineNamed(arg);
            if (pipeline) {
                await openFlow(estate, documents, pipeline);
            }
        }
    });

    command('sqlflow.openRun', async (arg: unknown) => {
        const runId = arg instanceof RunNode ? arg.run.runId : arg;
        if (typeof runId === 'string') {
            await showRun(estate, panels, runId, async (run) =>
                (await Promise.all(runSections.map((sections) => sections(run)))).flat());
        }
    });

    command('sqlflow.cancelRun', async (arg: unknown) => {
        if (arg instanceof RunNode) {
            await estate.cancelRun(arg.run.runId);
            void vscode.window.showInformationMessage(`${host.product}: cancellation requested for run ${arg.run.runId}.`);
            runs.refresh();
        }
    });

    command('sqlflow.runSchedule', async (arg: unknown) => {
        if (arg instanceof ScheduleNode) {
            await mcp.text('run_schedule', { id: arg.schedule.id });
            void vscode.window.showInformationMessage(`${host.product}: ${arg.schedule.name ?? 'the schedule'} fired.`);
            runs.refresh();
            schedules.refresh();
        }
    });

    command('sqlflow.showSchedulePlan', async (arg: unknown) => {
        if (arg instanceof ScheduleNode) {
            const plan = await mcp.call('get_schedule_plan', { id: arg.schedule.id });
            panels.show(`schedule:${arg.schedule.id}`, {
                title: arg.schedule.name ?? arg.schedule.id,
                subtitle: [arg.schedule.cron, arg.schedule.timezone].filter(Boolean).join(' '),
                sections: [
                    {
                        heading: 'Schedule',
                        facts: [
                            ['Next', arg.schedule.nextFireUtc],
                            ['Last', arg.schedule.lastFireUtc],
                            ['Paused', arg.schedule.paused ? 'yes' : 'no'],
                            ['After', arg.schedule.afterSchedules?.join(', ')],
                        ],
                    },
                    { heading: 'What it would run', code: JSON.stringify(plan, null, 2) },
                ],
            });
        }
    });

    command('sqlflow.openDataStream', async (arg: unknown) => {
        if (arg instanceof DataStreamNode) {
            const days = vscode.workspace.getConfiguration('sqlflow').get<number>('dataStreamWindowDays', 60);
            // The full analysis, including the day-by-day series and every detector's reasoning (the ones
            // that stayed quiet included, so a verdict can be checked rather than taken on trust).
            DataStreamPanel.show(await estate.dataStream(arg.stream.pipelineId, days), days);
        }
    });

    command('sqlflow.showLineage', async (arg: unknown) => {
        if (arg instanceof RepoNode) {
            panels.show(`lineage:${arg.repo.id}`, lineageContent(arg.repo.name, await estate.lineageWaves(arg.repo.id)));
        }
    });

    command('sqlflow.compareWithSynced', async (arg: unknown) => {
        const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
        if (uri) {
            await compareWithSynced(estate, documents, uri);
        }
    });

    command('sqlflow.showFlowHistory', async (arg: unknown) => {
        const resolved = await resolvePipeline(arg);
        if (resolved) {
            panels.show(`history:${resolved.pipeline.id}`, historyContent(resolved.pipeline, await estate.flowFileHistory(resolved.pipeline.id)));
        }
    });

    command('sqlflow.openInGui', async (arg: unknown) => {
        const resolved = await resolvePipeline(arg);
        const page = resolved?.pipeline.links?.page;
        if (!page) {
            void vscode.window.showInformationMessage(`${host.product}: set sqlflow.guiUrl to open the GUI from here.`);
            return;
        }
        await vscode.env.openExternal(vscode.Uri.parse(page));
    });

    register('sqlflow.setupMcp', () => connectAssistant(
        { providerId: host.mcp.providerId, product: host.product, program: host.mcp.program, version },
        mcp
    ));

    register('sqlflow.showStatus', async () => {
        const signedIn = signIn.who !== undefined;
        const pick = await vscode.window.showQuickPick(
            [
                signedIn
                    ? { label: '$(sign-out) Sign Out', id: 'sqlflow.signOut' }
                    : { label: '$(sign-in) Sign In with the Browser', id: 'sqlflow.signIn' },
                ...(signedIn ? [] : [{ label: '$(key) Sign In with an Access Token', id: 'sqlflow.signInWithToken' }]),
                { label: '$(gear) Set Control Plane URL', id: 'sqlflow.setControlPlaneUrl' },
                { label: '$(hubot) Connect an AI Assistant', id: 'sqlflow.setupMcp' },
                { label: '$(checklist) Validate Repository', id: 'sqlflow.validateRepository' },
                { label: '$(book) Search Docs', id: 'sqlflow.searchDocs' },
            ],
            { placeHolder: signedIn ? `${host.product}: signed in as ${signIn.who}` : `${host.product}: not signed in` }
        );
        if (pick) {
            await vscode.commands.executeCommand(pick.id);
        }
    });

    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
    status.command = 'sqlflow.showStatus';
    const showStatus = (): void => {
        status.text = signIn.who ? `$(account) ${host.product}` : `$(sign-in) ${host.product}: sign in`;
        status.tooltip = signIn.who ? `Signed in as ${signIn.who}` : 'Sign in with the browser';
        status.show();
    };
    context.subscriptions.push(status, signIn.changed(showStatus));
    showStatus();

    context.subscriptions.push(
        recogniseOpenedDocuments(),
        vscode.languages.registerCompletionItemProvider(DOCUMENT_SELECTOR, references, ':', ' '),
        vscode.languages.registerDocumentLinkProvider(DOCUMENT_SELECTOR, references),
        vscode.languages.registerDefinitionProvider(DOCUMENT_SELECTOR, references),
        vscode.languages.registerCodeLensProvider(DOCUMENT_SELECTOR, lenses),
        vscode.workspace.registerTextDocumentContentProvider(SYNCED_SCHEME, new SyncedDefinitions(estate)),
        vscode.tasks.registerTaskProvider(TASK_TYPE, cliTaskProvider()),
        registerWithEditor(
            { providerId: host.mcp.providerId, product: host.product, program: host.mcp.program, version },
            mcp,
            onServerChange.event
        )
    );

    const moduleContext: ModuleContext = {
        extension: context,
        product: host.product,
        estate,
        documents,
        panels,
        output,
        documentView,
        references,
        lenses,
        command,
        refreshed: onRefresh.event,
        addRunSections: (sections) => runSections.push(sections),
        addRunParameters: (parameters) => runParameters.push(parameters),
    };
    for (const module of host.modules ?? []) {
        module.activate(moduleContext);
    }

    // Start the language server (optional; every other feature works without it).
    void startLspClient(context, output, {
        searchPaths: host.lspSearchPaths(extensionPath),
        censusDirectories: host.censusDirectories(extensionPath),
    });

    void signIn.check();
    output.info(`${host.product} extension activated.`);
    return { estate };
}

export async function deactivateHost(): Promise<void> {
    await stopLspClient();
}

async function pickSource(estate: Estate): Promise<{ id: string; name: string } | undefined> {
    const sources = await estate.repoSources();
    if (sources.length === 1) {
        return sources[0];
    }
    const chosen = await vscode.window.showQuickPick(
        sources.map((source) => ({ label: source.name, description: source.lastSyncedSha?.slice(0, 8), source })),
        { placeHolder: 'Sync which repository?' }
    );
    return chosen?.source;
}

/** Opens a flow: its local file when this workspace holds it, else what the estate holds. */
async function openFlow(estate: Estate, documents: DocumentIndex, pipeline: Pipeline): Promise<void> {
    const local = (await documents.all()).find((document) => document.kind === 'flow' && document.name === pipeline.name);
    if (local) {
        await vscode.window.showTextDocument(local.uri, { preview: true });
        return;
    }
    const detail = await estate.pipeline(pipeline.id);
    const document = await vscode.workspace.openTextDocument({ language: 'sqlflow-flow', content: detail.yaml });
    await vscode.window.showTextDocument(document, { preview: true });
}

function lineageContent(repo: string, answer: unknown): PanelContent {
    const body = answer as { items?: { wave: number; pipelines: { name: string; kind: string }[] }[] } | { wave: number; pipelines: { name: string; kind: string }[] }[];
    const waves = Array.isArray(body) ? body : body.items ?? [];
    return {
        title: `${repo}: run order`,
        subtitle: 'Flows of a wave run together; a later wave runs after every earlier one ends.',
        sections: [{
            heading: 'Waves',
            columns: ['Wave', 'Flow', 'Kind'],
            rows: waves.flatMap((wave) => wave.pipelines.map((pipeline) => [
                wave.wave,
                { text: pipeline.name, command: 'sqlflow.openFlowByName', args: [pipeline.name] },
                pipeline.kind,
            ])),
            empty: 'Lineage has not been computed for this repository yet.',
        }],
    };
}

function historyContent(pipeline: Pipeline, answer: unknown): PanelContent {
    const body = answer as { commits?: Record<string, unknown>[]; items?: Record<string, unknown>[] } | Record<string, unknown>[];
    const commits = Array.isArray(body) ? body : body.commits ?? body.items ?? [];
    return {
        title: `${pipeline.name}: history`,
        subtitle: pipeline.relativePath ? path.posix.basename(pipeline.relativePath) : undefined,
        sections: [{
            heading: 'Commits',
            columns: ['Commit', 'When', 'Who', 'Message'],
            rows: commits.map((commit) => [
                String(commit.sha ?? commit.commitSha ?? '').slice(0, 8),
                String(commit.authoredUtc ?? commit.committedUtc ?? commit.date ?? ''),
                String(commit.author ?? commit.authorName ?? ''),
                String(commit.subject ?? commit.message ?? ''),
            ]),
            empty: 'The repository holds no history for this file.',
        }],
    };
}
