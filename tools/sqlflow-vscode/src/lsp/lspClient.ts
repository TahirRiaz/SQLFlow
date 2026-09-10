import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

/**
 * Resolve the sqlflow-lsp binary: the `sqlflow.lspBinaryPath` setting first,
 * then the bundled `bin/` copy, then the debug build under the Cargo target
 * (so the extension works from a checkout without a packaging step).
 */
function findLspBinary(context: vscode.ExtensionContext): string | null {
    const explicit = vscode.workspace.getConfiguration('sqlflow').get<string>('lspBinaryPath', '');
    if (explicit && fs.existsSync(explicit)) {
        return explicit;
    }
    const exe = process.platform === 'win32' ? 'sqlflow-lsp.exe' : 'sqlflow-lsp';
    const candidates = [
        path.join(context.extensionPath, 'bin', exe),
        path.join(context.extensionPath, '..', 'target', 'release', exe),
        path.join(context.extensionPath, '..', 'target', 'debug', exe),
    ];
    return candidates.find((p) => fs.existsSync(p)) ?? null;
}

/**
 * Start the language server as a stdio child process. Returns false (with a
 * logged reason) when the binary is missing; the rest of the extension keeps
 * working without language intelligence.
 */
export async function startLspClient(
    context: vscode.ExtensionContext,
    output: vscode.OutputChannel
): Promise<boolean> {
    const binary = findLspBinary(context);
    if (!binary) {
        output.appendLine('[lsp] sqlflow-lsp binary not found; language features disabled. Build with: cargo build -p sqlflow-lsp');
        return false;
    }
    output.appendLine(`[lsp] using ${binary}`);

    if (process.platform !== 'win32') {
        try {
            fs.chmodSync(binary, 0o755);
        } catch (err) {
            output.appendLine(`[lsp] could not set +x on ${binary}: ${err}`);
        }
    }

    const serverOptions: ServerOptions = {
        run: { command: binary, transport: TransportKind.stdio },
        debug: { command: binary, transport: TransportKind.stdio },
    };
    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'sqlflow-flow' },
            { scheme: 'untitled', language: 'sqlflow-flow' },
        ],
        outputChannel: output,
    };

    client = new LanguageClient('sqlflowLsp', 'SQLFlow Language Server', serverOptions, clientOptions);
    try {
        await client.start();
        context.subscriptions.push(client);
        output.appendLine('[lsp] language server started');
        return true;
    } catch (err) {
        output.appendLine(`[lsp] failed to start: ${err}`);
        client = undefined;
        return false;
    }
}

export async function stopLspClient(): Promise<void> {
    if (client) {
        await client.stop();
        client = undefined;
    }
}
