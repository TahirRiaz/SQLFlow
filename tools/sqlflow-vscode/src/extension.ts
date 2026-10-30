import * as path from 'path';
import * as vscode from 'vscode';
import { activateHost, deactivateHost, VscodeHost } from './host';

/** The Cargo target beside this folder, where a checkout's builds land. */
function buildFolders(extensionPath: string): string[] {
    return ['release', 'debug'].map((profile) => path.join(extensionPath, '..', 'target', profile));
}

/** SQLFlow's own extension: the host with no modules. */
const SQLFLOW: VscodeHost = {
    product: 'SQLFlow',
    mcp: {
        program: 'sqlflow-mcp',
        statePrefix: 'mcp',
        providerId: 'sqlflow.mcp',
        searchPaths: buildFolders,
    },
    lspSearchPaths: buildFolders,
    censusDirectories: () => [],
    docCorpora: (extensionPath) => [path.join(extensionPath, 'dist', 'reference')],
    skeletons: [
        {
            label: 'Ingestion flow',
            detail: 'flowType: ing, table to table',
            content: 'flowType: ing\nname: newFlow\nsource:\n  server: src\n  table: dbo.source\ntarget:\n  server: dwh\n  table: stg.target\nload:\n  mode: append\n',
        },
        {
            label: 'File flow',
            detail: 'Files into a table',
            content: 'name: newFlow\nsource:\n  type: csv\n  path: ./data/*.csv\ntarget:\n  server: dwh\n  table: stg.newFlow\n',
        },
        {
            label: 'Schedule library',
            detail: 'schedules.yaml, named schedules flows join',
            content: 'schedules:\n  nightly:\n    cron: "0 1 * * *"\n    timezone: UTC\n',
        },
        {
            label: 'AI context',
            detail: '*.ai-context.yaml, what an assistant may use once an admin approves it',
            content: 'aiContext: sales\ndescription: What the context covers\ntables:\n  - table: dw.sales.Orders\n    name: Order lines\n    description: One row per order line.\n',
        },
    ],
    cliVerbs: [
        {
            verb: 'validate',
            label: 'Validate',
            detail: 'Parse and validate this document through the loader a run uses, offline.',
            applies: () => true,
        },
    ],
};

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    await activateHost(context, SQLFLOW);
}

export async function deactivate(): Promise<void> {
    await deactivateHost();
}
