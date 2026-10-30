import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { JsonRpcClient } from './jsonRpc';
import { jsonOf, textOf, ToolFailed } from './toolAnswer';

/** How much of a server's stderr is kept, to say why it stopped. */
const STDERR_TAIL = 2000;

export interface McpServerOptions {
    /** The program, as a host names it (`sqlflow-mcp`). */
    program: string;
    /** Where a build of the program may be, in order, beside the setting and `bin/`. */
    searchPaths: string[];
    extensionPath: string;
    version: string;
    output: vscode.LogOutputChannel;
}

/** The environment a server runs under: the control plane and GUI the settings name, else what it was last given. */
export function serverEnvironment(): Record<string, string> {
    const config = vscode.workspace.getConfiguration('sqlflow');
    const env: Record<string, string> = {};
    const controlPlane = config.get<string>('controlPlaneUrl', '').trim();
    if (controlPlane !== '') {
        env.SQLFLOW_CONTROL_PLANE_URL = controlPlane;
    }
    const gui = config.get<string>('guiUrl', '').trim();
    if (gui !== '') {
        env.SQLFLOW_GUI_URL = gui;
    }
    const cli = config.get<string>('cliPath', '').trim();
    if (cli !== '') {
        env.SQLFLOW_CLI = cli;
    }
    return env;
}

/**
 * The server: this extension's one way to the estate. Every view, panel and command reads through the tools an assistant
 * calls, so the extension and the assistant beside it see one estate under one sign-in, with one set of rules about what
 * is metadata. It starts the program on first use, keeps one session and starts another if the program stops.
 */
export class McpServer implements vscode.Disposable {
    private readonly options: McpServerOptions;
    private readonly onStateChange = new vscode.EventEmitter<void>();
    /** Fires when the server starts or stops. */
    readonly stateChanged = this.onStateChange.event;
    private process: ChildProcessWithoutNullStreams | undefined;
    private rpc: JsonRpcClient | undefined;
    private starting: Promise<JsonRpcClient> | undefined;
    private tools: Set<string> | undefined;
    private stderr = '';
    private disposed = false;

    constructor(options: McpServerOptions) {
        this.options = options;
    }

    /** The program's path, or null when no build of it can be found. */
    binary(): string | null {
        const exe = process.platform === 'win32' ? `${this.options.program}.exe` : this.options.program;
        const configured = vscode.workspace.getConfiguration('sqlflow').get<string>('mcpBinaryPath', '').trim();
        const candidates = [
            ...(configured === '' ? [] : [configured]),
            path.join(this.options.extensionPath, 'bin', exe),
            ...this.options.searchPaths.map((folder) => path.join(folder, exe)),
        ];
        return candidates.find((candidate) => fs.existsSync(candidate)) ?? null;
    }

    get running(): boolean {
        return this.rpc !== undefined;
    }

    /** Whether the server offers a tool: a host leaves some of SQLFlow's out. */
    async offers(tool: string): Promise<boolean> {
        await this.session();
        return this.tools?.has(tool) ?? false;
    }

    /** A tool's answer as text, for the tools that answer in sentences. */
    async text(tool: string, args: Record<string, unknown> = {}): Promise<string> {
        const rpc = await this.session();
        return textOf(tool, await rpc.request('tools/call', { name: tool, arguments: withoutEmpty(args) }));
    }

    /** A tool's answer as the JSON it is. */
    async call<T>(tool: string, args: Record<string, unknown> = {}): Promise<T> {
        return jsonOf<T>(tool, await this.text(tool, args));
    }

    /** Ends the session, so the next call starts a server that reads the settings and the sign-in again. */
    restart(): void {
        this.stop('The server was restarted.');
    }

    dispose(): void {
        this.disposed = true;
        this.stop('The extension stopped.');
        this.onStateChange.dispose();
    }

    private stop(reason: string): void {
        this.rpc?.close(reason);
        this.rpc = undefined;
        this.tools = undefined;
        this.starting = undefined;
        const running = this.process;
        this.process = undefined;
        running?.stdin.end();
        running?.kill();
        if (!this.disposed) {
            this.onStateChange.fire();
        }
    }

    private session(): Promise<JsonRpcClient> {
        if (this.rpc) {
            return Promise.resolve(this.rpc);
        }
        this.starting ??= this.start().finally(() => (this.starting = undefined));
        return this.starting;
    }

    private async start(): Promise<JsonRpcClient> {
        if (this.disposed) {
            throw new ToolFailed('start', 'The extension stopped.');
        }
        const binary = this.binary();
        if (!binary) {
            throw new ToolFailed(
                'start',
                `The ${this.options.program} program was not found. Install the packaged extension, or set sqlflow.mcpBinaryPath to a build of it.`
            );
        }

        this.options.output.info(`[mcp] starting ${binary}`);
        this.stderr = '';
        const child = spawn(binary, [], { env: { ...process.env, ...serverEnvironment() }, stdio: 'pipe', windowsHide: true });
        const rpc = new JsonRpcClient((line) => child.stdin.write(`${line}\n`));
        child.stdout.setEncoding('utf8').on('data', (chunk: string) => rpc.receive(chunk));
        child.stderr.setEncoding('utf8').on('data', (chunk: string) => {
            this.stderr = (this.stderr + chunk).slice(-STDERR_TAIL);
            this.options.output.debug(`[mcp] ${chunk.trimEnd()}`);
        });
        child.on('error', (error) => this.stopped(child, rpc, `${binary} could not start: ${error.message}`));
        child.on('exit', (code) => this.stopped(child, rpc, `${binary} stopped (exit ${code}). ${this.stderr}`.trim()));
        child.stdin.on('error', (error) => this.options.output.warn(`[mcp] ${error.message}`));
        this.process = child;

        const initialized = (await rpc.request('initialize', {
            protocolVersion: '2025-06-18',
            capabilities: {},
            clientInfo: { name: this.options.program, version: this.options.version },
        })) as { serverInfo?: { name?: string; version?: string } };
        rpc.notify('notifications/initialized');
        const listed = (await rpc.request('tools/list', {})) as { tools?: { name: string }[] };
        this.tools = new Set((listed.tools ?? []).map((tool) => tool.name));
        this.rpc = rpc;
        this.options.output.info(`[mcp] ${initialized.serverInfo?.name} ${initialized.serverInfo?.version} offers ${this.tools.size} tools`);
        this.onStateChange.fire();
        return rpc;
    }

    private stopped(child: ChildProcessWithoutNullStreams, rpc: JsonRpcClient, reason: string): void {
        rpc.close(reason);
        if (this.process !== child) {
            return;
        }
        this.options.output.warn(`[mcp] ${reason}`);
        this.process = undefined;
        this.rpc = undefined;
        this.tools = undefined;
        if (!this.disposed) {
            this.onStateChange.fire();
        }
    }
}

/** An argument nobody gave stays off the call: the tools read a missing argument, and refuse an empty id. */
function withoutEmpty(args: Record<string, unknown>): Record<string, unknown> {
    return Object.fromEntries(Object.entries(args).filter(([, value]) => value !== undefined && value !== null && value !== ''));
}
