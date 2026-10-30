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

/** Where the language server runs from, and what a host adds to what it knows. */
export interface LspOptions {
    /** Folders that may hold a build, beside the setting and `bin/`. */
    searchPaths: string[];
    /** The key census files of the flow kinds and documents the host's modules add. */
    censusDirectories: string[];
}

/**
 * Resolve the sqlflow-lsp binary: the `sqlflow.lspBinaryPath` setting first,
 * then the bundled `bin/` copy, then the host's build folders (so the extension
 * works from a checkout without a packaging step).
 */
function findLspBinary(context: vscode.ExtensionContext, options: LspOptions): string | null {
    const explicit = vscode.workspace.getConfiguration('sqlflow').get<string>('lspBinaryPath', '');
    if (explicit && fs.existsSync(explicit)) {
        return explicit;
    }
    const exe = process.platform === 'win32' ? 'sqlflow-lsp.exe' : 'sqlflow-lsp';
    const candidates = [
        path.join(context.extensionPath, 'bin', exe),
        ...options.searchPaths.map((folder) => path.join(folder, exe)),
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
    output: vscode.LogOutputChannel,
    options: LspOptions
): Promise<boolean> {
    const binary = findLspBinary(context, options);
    if (!binary) {
        output.warn('[lsp] sqlflow-lsp binary not found; language features disabled. Build with: cargo build -p sqlflow-lsp');
        return false;
    }
    output.info(`[lsp] using ${binary}`);

    if (process.platform !== 'win32') {
        try {
            fs.chmodSync(binary, 0o755);
        } catch (err) {
            output.warn(`[lsp] could not set +x on ${binary}: ${err}`);
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
        initializationOptions: { censusDirectories: options.censusDirectories },
        outputChannel: output,
    };

    client = new LanguageClient('sqlflowLsp', 'SQLFlow Language Server', serverOptions, clientOptions);
    try {
        await client.start();
        context.subscriptions.push(client);
        output.info('[lsp] language server started');
        return true;
    } catch (err) {
        output.error(`[lsp] failed to start: ${err}`);
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
