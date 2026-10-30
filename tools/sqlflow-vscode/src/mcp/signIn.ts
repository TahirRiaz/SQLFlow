import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { McpServer } from './mcpServer';
import { parseAuthStatus, parseSignIn, ToolFailed } from './toolAnswer';

/** The longest a browser sign-in is waited for: the code's own life is the control plane's, and it says when it ends. */
const SIGN_IN_WAIT_MS = 10 * 60 * 1000;
/** A little past the five seconds the device grant asks, since a poll sooner than that is told to slow down. */
const POLL_MS = 5500;

/**
 * Who is signed in. The sign-in belongs to the MCP server: it keeps the session, renews it and sends it with every call,
 * so a person signs in once for this extension and for every assistant that uses the same server. Signing in here is the
 * server's device grant with the browser in front of it: the browser opens the control plane's approval, which goes on
 * to the GUI, where the person signs in the way they always do (Microsoft Entra single sign-on, or an account), and
 * approves the code this editor shows.
 */
export class SignIn implements vscode.Disposable {
    private readonly mcp: McpServer;
    private readonly product: string;
    private readonly onChange = new vscode.EventEmitter<void>();
    readonly changed = this.onChange.event;
    private person: string | undefined;
    private readonly watch: fs.FSWatcher | undefined;
    private readonly tokenFile: string;

    constructor(mcp: McpServer, product: string, statePrefix: string) {
        this.mcp = mcp;
        this.product = product;
        this.tokenFile = path.join(STATE_FOLDER, `${statePrefix}-token.json`);
        // Another window, or an assistant's copy of the server, may sign in or out: every server reads the same file.
        this.watch = watchFile(this.tokenFile, () => void this.tokenChanged());
    }

    dispose(): void {
        this.watch?.close();
        this.onChange.dispose();
    }

    /** The person signed in, as last checked. */
    get who(): string | undefined {
        return this.person;
    }

    /** Asks the server who its credential is. Reachable or not, this never throws: no answer reads as signed out. */
    async check(): Promise<boolean> {
        try {
            const status = parseAuthStatus(await this.mcp.text('check_auth_status'));
            this.set(status.state === 'signed-in' ? status.who ?? 'signed in' : undefined);
        } catch {
            this.set(undefined);
        }
        return this.person !== undefined;
    }

    private set(person: string | undefined): void {
        const changed = person !== this.person;
        this.person = person;
        void vscode.commands.executeCommand('setContext', 'sqlflow.signedIn', person !== undefined);
        if (changed) {
            this.onChange.fire();
        }
    }

    /** A server here holds the sign-in it read when it started, so one made or ended elsewhere needs a new session. A
     * file that only changed is a session renewing, which every server does for itself. */
    private async tokenChanged(): Promise<void> {
        if (fs.existsSync(this.tokenFile) !== (this.person !== undefined)) {
            this.mcp.restart();
            await this.check();
        }
    }

    /** Signs in through the browser. */
    async signIn(): Promise<boolean> {
        let request;
        try {
            request = parseSignIn(await this.mcp.text('login'));
        } catch (err) {
            void vscode.window.showErrorMessage(`${this.product}: sign-in could not start. ${err instanceof Error ? err.message : err}`);
            return false;
        }

        await vscode.env.clipboard.writeText(request.code);
        await vscode.env.openExternal(vscode.Uri.parse(request.url));
        return vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title: `${this.product}: approve the sign-in in your browser. Code ${request.code} (copied)`,
                cancellable: true,
            },
            (_progress, cancel) => this.waitForApproval(cancel)
        );
    }

    private async waitForApproval(cancel: vscode.CancellationToken): Promise<boolean> {
        const deadline = Date.now() + SIGN_IN_WAIT_MS;
        let wait = POLL_MS;
        while (Date.now() < deadline && !cancel.isCancellationRequested) {
            await new Promise((resolve) => setTimeout(resolve, wait));
            let status;
            try {
                status = parseAuthStatus(await this.mcp.text('check_auth_status'));
            } catch (err) {
                if (err instanceof ToolFailed) {
                    continue;
                }
                throw err;
            }
            switch (status.state) {
                case 'signed-in':
                    await this.check();
                    void vscode.window.showInformationMessage(`${this.product}: signed in as ${this.person}.`);
                    return true;
                case 'slow-down':
                    wait += POLL_MS;
                    break;
                case 'denied':
                case 'expired':
                    void vscode.window.showWarningMessage(`${this.product}: the sign-in was ${status.state === 'denied' ? 'denied' : 'not approved in time'}.`);
                    return false;
                default:
                    break;
            }
        }
        return false;
    }

    /** Signs in with a token a person pasted, for a place no browser reaches. */
    async signInWithToken(): Promise<boolean> {
        const token = await vscode.window.showInputBox({
            prompt: `Paste a ${this.product} access token`,
            password: true,
            ignoreFocusOut: true,
        });
        if (!token) {
            return false;
        }
        try {
            await this.mcp.text('set_access_token', { token: token.trim() });
        } catch (err) {
            void vscode.window.showErrorMessage(`${this.product}: the token was not taken. ${err instanceof Error ? err.message : err}`);
            return false;
        }
        return this.check();
    }

    async signOut(): Promise<void> {
        await this.mcp.text('logout');
        this.set(undefined);
        void vscode.window.showInformationMessage(`${this.product}: signed out.`);
    }
}

/** Where every server of this machine keeps its address and its sign-in. */
const STATE_FOLDER = path.join(os.homedir(), '.sqlflow');

/** Watches a file by its folder, which outlives it. A folder that is not there yet means nobody has signed in on this
 * machine; the sign-in here makes it, and this window already knows. */
function watchFile(file: string, changed: () => void): fs.FSWatcher | undefined {
    try {
        let timer: NodeJS.Timeout | undefined;
        return fs.watch(path.dirname(file), (_event, name) => {
            if (name === path.basename(file)) {
                clearTimeout(timer);
                timer = setTimeout(changed, 500);
            }
        });
    } catch {
        return undefined;
    }
}
