import * as vscode from 'vscode';
import * as os from 'os';

const TOKEN_KEY = 'sqlflow.accessToken';
const META_KEY = 'sqlflow.accessTokenMeta';

/** The lifetime a self-managed PAT is minted with, and how close to expiry it is rotated. */
const PAT_LIFETIME_DAYS = 90;
const PAT_ROTATE_WINDOW_DAYS = 14;
const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Metadata about the stored credential the plain token string cannot carry: whether it is a personal access token
 * this extension mints and rotates on its own, and (when so) its id and expiry so it can be rotated before it lapses
 * and its predecessor revoked.
 */
interface TokenMeta {
    /** The token's catalog id, present only for a managed PAT we minted (so we can revoke it after rotating). */
    id?: string;
    /** ISO-8601 expiry, present only for a managed PAT with a bounded lifetime. */
    expiresAt?: string;
    /** The scopes the token carries, requested again when rotating so the replacement keeps the same grant. */
    scopes: string[];
    /** True for a managed PAT we rotate on our own; false for a pasted secret or a raw device token. */
    renewable: boolean;
}

/**
 * Holds the control-plane access token in VS Code SecretStorage. An interactive sign-in (device flow, or a pasted
 * session token) is immediately exchanged for a long-lived personal access token that this class rotates on its own
 * before it expires, so the user does not have to sign in again while the extension stays in use. A pasted PAT is
 * held as-is (the user manages its lifecycle).
 */
export class Session {
    private readonly onChange = new vscode.EventEmitter<void>();
    readonly changed = this.onChange.event;
    /** Single-flights rotation so two concurrent requests entering the window mint one replacement, not two. */
    private rotating = false;

    constructor(private readonly secrets: vscode.SecretStorage) {}

    async getToken(): Promise<string | undefined> {
        return this.secrets.get(TOKEN_KEY);
    }

    private async getMeta(): Promise<TokenMeta | undefined> {
        const raw = await this.secrets.get(META_KEY);
        if (!raw) {
            return undefined;
        }
        try {
            return JSON.parse(raw) as TokenMeta;
        } catch {
            return undefined;
        }
    }

    async setToken(token: string, meta?: TokenMeta): Promise<void> {
        await this.secrets.store(TOKEN_KEY, token);
        if (meta) {
            await this.secrets.store(META_KEY, JSON.stringify(meta));
        } else {
            await this.secrets.delete(META_KEY);
        }
        this.onChange.fire();
    }

    async clear(): Promise<void> {
        await this.secrets.delete(TOKEN_KEY);
        await this.secrets.delete(META_KEY);
        this.onChange.fire();
    }

    async isSignedIn(): Promise<boolean> {
        return (await this.getToken()) !== undefined;
    }

    /**
     * Rotate the managed token if it has entered its rotation window: mint a replacement with the still-valid
     * current token, store it, then revoke the old one. A cheap no-op when nothing is due (the common case), so it
     * is safe to call before every request. Never throws: a failed rotation leaves the current token in place, and
     * a genuinely expired token is handled by the normal 401 -> sign-in path.
     */
    async ensureFresh(baseUrl: string): Promise<void> {
        const meta = await this.getMeta();
        if (!meta?.renewable || !meta.id || !meta.expiresAt) {
            return;
        }
        const expMs = Date.parse(meta.expiresAt);
        const now = Date.now();
        const inWindow = now < expMs && now + PAT_ROTATE_WINDOW_DAYS * DAY_MS >= expMs;
        if (!inWindow || this.rotating) {
            return;
        }

        this.rotating = true;
        try {
            const oldId = meta.id;
            if (await this.provisionPat(baseUrl, meta.scopes)) {
                // The freshly minted token is now current; revoke its predecessor with it. Best-effort: an orphaned
                // old token simply expires on its own.
                const token = await this.getToken();
                if (token) {
                    await fetch(`${baseUrl}/api/v1/me/tokens/${oldId}`, {
                        method: 'DELETE',
                        headers: { authorization: `Bearer ${token}` },
                    }).catch(() => undefined);
                }
            }
        } finally {
            this.rotating = false;
        }
    }

    /**
     * Exchange the current credential for a long-lived, self-managed PAT and store it in place. Best-effort: returns
     * false (leaving the current token untouched) if the control plane cannot mint one, so sign-in still works
     * against an older control plane or a session with no user behind it.
     */
    private async provisionPat(baseUrl: string, scopes: string[]): Promise<boolean> {
        const token = await this.getToken();
        if (!token) {
            return false;
        }
        try {
            const resp = await fetch(`${baseUrl}/api/v1/me/tokens`, {
                method: 'POST',
                headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
                body: JSON.stringify({ name: `sqlflow-vscode (${os.hostname()})`, scopes, expiresInDays: PAT_LIFETIME_DAYS }),
            });
            if (!resp.ok) {
                return false;
            }
            const body = (await resp.json()) as { token: { id: string; scopes: string[]; expiresUtc: string | null }; secret: string };
            await this.setToken(body.secret, {
                id: body.token.id,
                expiresAt: body.token.expiresUtc ?? undefined,
                scopes: body.token.scopes,
                renewable: true,
            });
            return true;
        } catch {
            return false;
        }
    }

    /** Sign in via device flow, or fall back to pasting a token. */
    async signIn(baseUrl: string): Promise<boolean> {
        const method = await vscode.window.showQuickPick(
            [
                { label: 'Sign in with a browser', detail: 'Device authorization: approve in the SQLFlow web page.', id: 'device' },
                { label: 'Paste an access token', detail: 'Use a token from the CLI or GUI.', id: 'token' },
            ],
            { placeHolder: 'How would you like to sign in?' }
        );
        if (!method) {
            return false;
        }
        return method.id === 'token' ? this.signInWithToken(baseUrl) : this.signInWithDevice(baseUrl);
    }

    private async signInWithToken(baseUrl: string): Promise<boolean> {
        const input = await vscode.window.showInputBox({
            prompt: 'Paste a SQLFlow control-plane bearer token',
            password: true,
            ignoreFocusOut: true,
        });
        if (!input) {
            return false;
        }
        const token = input.trim();
        // A pasted personal access token (sqlf_…) is already long-lived and owned by the user, so hold it as-is. A
        // pasted session token is short-lived, so exchange it for one this extension manages and rotates.
        if (token.startsWith('sqlf_')) {
            await this.setToken(token, { scopes: [], renewable: false });
        } else {
            await this.setToken(token, { scopes: ['read', 'operate'], renewable: false });
            await this.provisionPat(baseUrl, ['read', 'operate']);
        }
        return true;
    }

    private async signInWithDevice(baseUrl: string): Promise<boolean> {
        let auth: DeviceAuth;
        try {
            const resp = await fetch(`${baseUrl}/api/v1/auth/device`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ clientId: 'sqlflow-vscode', scope: 'read operate' }),
            });
            if (!resp.ok) {
                throw new Error(`device authorization failed (${resp.status})`);
            }
            auth = (await resp.json()) as DeviceAuth;
        } catch (err) {
            void vscode.window.showErrorMessage(`SQLFlow sign-in could not start: ${errText(err)}`);
            return false;
        }

        const openUrl = auth.verificationUriComplete ?? auth.verificationUri;
        await vscode.env.openExternal(vscode.Uri.parse(openUrl));

        return vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title: `SQLFlow: approve sign-in — code ${auth.userCode}`,
                cancellable: true,
            },
            async (_progress, cancel) => {
                const deadline = Date.now() + auth.expiresIn * 1000;
                let interval = Math.max(1, auth.interval) * 1000;
                while (Date.now() < deadline && !cancel.isCancellationRequested) {
                    await delay(interval);
                    const outcome = await this.pollDevice(baseUrl, auth.deviceCode);
                    if (outcome === 'approved') {
                        void vscode.window.showInformationMessage('SQLFlow: signed in.');
                        return true;
                    }
                    if (outcome === 'slow_down') {
                        interval += 2000;
                    }
                    if (outcome === 'denied' || outcome === 'expired') {
                        void vscode.window.showWarningMessage(`SQLFlow sign-in ${outcome}.`);
                        return false;
                    }
                }
                return false;
            }
        );
    }

    private async pollDevice(baseUrl: string, deviceCode: string): Promise<PollOutcome> {
        try {
            const resp = await fetch(`${baseUrl}/api/v1/auth/device/token`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ deviceCode }),
            });
            if (resp.ok) {
                const body = (await resp.json()) as { access_token: string };
                // Store the short-lived device token, then exchange it for a long-lived, self-rotating PAT so the
                // user does not have to sign in again. If the exchange fails, the device token stands.
                await this.setToken(body.access_token, { scopes: ['read', 'operate'], renewable: false });
                await this.provisionPat(baseUrl, ['read', 'operate']);
                return 'approved';
            }
            const err = (await resp.json().catch(() => ({}))) as { error?: string };
            switch (err.error) {
                case 'slow_down':
                    return 'slow_down';
                case 'access_denied':
                    return 'denied';
                case 'expired_token':
                    return 'expired';
                default:
                    return 'pending';
            }
        } catch {
            return 'pending';
        }
    }
}

interface DeviceAuth {
    deviceCode: string;
    userCode: string;
    verificationUri: string;
    verificationUriComplete?: string;
    expiresIn: number;
    interval: number;
}

type PollOutcome = 'approved' | 'pending' | 'slow_down' | 'denied' | 'expired';

function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

export function errText(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}
