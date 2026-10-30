import * as vscode from 'vscode';
import { Estate, FlowKindOperation, Pipeline, Run } from '../mcp/estate';
import { isActive } from '../views/trees';
import { PanelContent, PanelSection } from '../views/panelContent';
import { Panels } from '../views/panels';

/** How often a run being followed is read. */
const FOLLOW_MS = 3000;

/** The entries of a run's trace a panel shows: the end of it, which holds how it finished. */
const TRACE_ENTRIES = 200;

interface RunArguments {
    fullLoad?: boolean;
    operation?: string;
    values?: Record<string, unknown>;
    payload?: Record<string, unknown>;
}

/** A parameter a run of a flow takes, as a module or the document declares it. */
export interface RunParameter {
    name: string;
    required?: boolean;
    defaultValue?: string;
    description?: string;
    /** The values it may take, offered as a choice. */
    options?: string[];
}

/** What a run of a kind may be given beyond its operation and parameters: undefined for nothing, null to cancel the run. */
export type RunPayloadReader = (pipeline: Pipeline, operation: string | undefined) => Promise<Record<string, unknown> | undefined | null>;

/** Asks what a run should do. A kind a host module registers says what its runs can do; SQLFlow's own kinds take a full
 * load or not. A run that writes to the flow's target is confirmed. */
async function askArguments(estate: Estate, pipeline: Pipeline, parameters: RunParameter[], payloadOf: RunPayloadReader): Promise<RunArguments | undefined> {
    const kinds = await estate.flowKinds();
    const kind = kinds.runOperations?.find((candidate) => candidate.flowType === pipeline.kind);
    if (!kind) {
        const load = await vscode.window.showQuickPick(['Incremental', 'Full load'], { placeHolder: `Run '${pipeline.name}'` });
        return load === undefined ? undefined : { fullLoad: load === 'Full load' };
    }

    const operation = kind.operations.length === 0
        ? undefined
        : await pickOperation(pipeline, kind.operations, kind.defaultOperation ?? undefined);
    if (kind.operations.length > 0 && !operation) {
        return undefined;
    }

    const values: Record<string, unknown> = {};
    for (const parameter of parameters) {
        const value = parameter.options
            ? await vscode.window.showQuickPick(parameter.options, { title: `Run '${pipeline.name}': ${parameter.name}`, ignoreFocusOut: true })
            : await vscode.window.showInputBox({
                title: `Run '${pipeline.name}': ${parameter.name}`,
                prompt: parameter.description,
                value: parameter.defaultValue,
                ignoreFocusOut: true,
                validateInput: (text) => (parameter.required && text.trim() === '' ? `${parameter.name} is required.` : undefined),
            });
        if (value === undefined) {
            return undefined;
        }
        if (value.trim() !== '') {
            values[parameter.name] = value.trim();
        }
    }

    const payload = await payloadOf(pipeline, operation?.name);
    if (payload === null) {
        return undefined;
    }

    if (operation?.writesTarget) {
        const confirmed = await vscode.window.showWarningMessage(
            `Run '${operation.label}' on ${pipeline.name}? This writes to the flow's target.`,
            { modal: true },
            'Run'
        );
        if (confirmed !== 'Run') {
            return undefined;
        }
    }

    return { operation: operation?.name, values: Object.keys(values).length > 0 ? values : undefined, payload };
}

async function pickOperation(pipeline: Pipeline, operations: FlowKindOperation[], defaultOperation: string | undefined): Promise<FlowKindOperation | undefined> {
    const sorted = [...operations].sort((a, b) => Number(b.name === defaultOperation) - Number(a.name === defaultOperation));
    const chosen = await vscode.window.showQuickPick(
        sorted.map((operation) => ({
            label: operation.label,
            description: operation.writesTarget ? 'writes to the target' : undefined,
            detail: operation.description,
            operation,
        })),
        { placeHolder: `Run '${pipeline.name}': what should it do?` }
    );
    return chosen?.operation;
}

/** Runs a flow: asks what the run should do, queues it through the same tool an assistant calls, and follows it. */
export async function runFlow(
    estate: Estate,
    product: string,
    pipeline: Pipeline,
    parameters: RunParameter[],
    payloadOf: RunPayloadReader,
    refreshed: () => void
): Promise<void> {
    const args = await askArguments(estate, pipeline, parameters, payloadOf);
    if (!args) {
        return;
    }
    const accepted = await estate.triggerRun({ repoId: pipeline.repoId, flowName: pipeline.name, ...args });
    refreshed();
    await follow(estate, product, accepted.runId, pipeline.name, refreshed);
}

/** Reads a run until it ends, says how it ended, and offers it. Closing the notification stops following, not the run. */
async function follow(estate: Estate, product: string, runId: string, flow: string, refreshed: () => void): Promise<void> {
    const ended = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `${product}: ${flow}`, cancellable: true },
        async (progress, cancel) => {
            let run: Run | undefined;
            while (!cancel.isCancellationRequested) {
                run = await estate.run(runId);
                if (!isActive(run)) {
                    return run;
                }
                progress.report({ message: run.lastAction ?? run.status });
                await new Promise((resolve) => setTimeout(resolve, FOLLOW_MS));
            }
            return undefined;
        }
    );
    refreshed();
    if (!ended) {
        return;
    }
    const show = ended.status === 'failed' ? vscode.window.showErrorMessage : vscode.window.showInformationMessage;
    const pick = await show(`${product}: ${flow} ${ended.status}.${ended.error ? ` ${ended.error}` : ''}`, 'Open Run');
    if (pick === 'Open Run') {
        await vscode.commands.executeCommand('sqlflow.openRun', runId);
    }
}

/** A run as a person reads it: how it went, then what it did. */
export async function showRun(estate: Estate, panels: Panels, runId: string, moduleSections: (run: Run) => Promise<PanelSection[]>): Promise<void> {
    const run = await estate.run(runId);
    const [trace, statements, assertions, files, added] = await Promise.all([
        estate.runTrace(runId, TRACE_ENTRIES),
        estate.runStatements(runId),
        estate.runAssertions(runId),
        estate.runFiles(runId),
        moduleSections(run),
    ]);

    const sections: PanelSection[] = [{
        heading: 'Run',
        facts: [
            ['Status', run.status],
            ['Operation', run.operation],
            ['Requested by', run.requestedBy ?? run.triggerSource],
            ['Started', run.startUtc],
            ['Ended', run.endUtc],
            ['Duration', run.durationSeconds === null || run.durationSeconds === undefined ? null : `${run.durationSeconds.toFixed(1)} s`],
            ['Rows', run.rowsLoaded],
            ['Commit', run.commitSha?.slice(0, 8)],
        ],
    }];
    if (run.error) {
        sections.push({ heading: 'Error', code: run.error });
    }
    if (run.failedStatementSql) {
        sections.push({ heading: `Statement that failed (${run.failedStatementStep ?? 'step unknown'})`, code: run.failedStatementSql });
    }
    sections.push(...added);
    sections.push({
        heading: trace.total > trace.entries.length ? `Trace (the last ${trace.entries.length} of ${trace.total} entries)` : 'Trace',
        columns: ['When', 'Level', 'Step', 'What happened'],
        rows: trace.entries.map((entry) => [
            entry.timestampUtc?.slice(11, 19),
            entry.level,
            entry.step,
            entry.error ?? entry.message ?? entry.sql,
        ]),
        empty: 'This run left no trace.',
    });
    if (assertions.length > 0) {
        sections.push({
            heading: 'Assertions',
            columns: ['Assertion', 'Result', 'Value', 'Error'],
            rows: assertions.map((assertion) => [assertion.name, assertion.result, assertion.assertedValue, assertion.error]),
        });
    }
    if (files.length > 0) {
        sections.push({
            heading: 'Files',
            columns: ['File', 'Rows', 'Bytes'],
            rows: files.map((file) => [file.name, file.rows, file.sizeBytes]),
        });
    }
    if (statements.length > 0) {
        sections.push({
            heading: 'Statements',
            columns: ['#', 'Step', 'Error'],
            rows: statements.map((statement) => [statement.ordinal, statement.step, statement.error]),
        });
    }

    const gui = run.links?.page ?? run.links?.run;
    const content: PanelContent = {
        title: `${run.flowName}: ${run.status}`,
        subtitle: run.runId,
        actions: gui ? [{ text: 'Open in GUI', url: gui }] : undefined,
        sections,
    };
    panels.show(`run:${runId}`, content);
}
