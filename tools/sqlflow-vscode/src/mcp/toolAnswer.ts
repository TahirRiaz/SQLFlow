/** A tool answered with a failure: the server refused the call, or the control plane did. */
export class ToolFailed extends Error {
    readonly tool: string;

    constructor(tool: string, message: string) {
        super(message);
        this.tool = tool;
    }
}

/** A tool needs a signed-in person and there is none: the one failure a client answers by offering to sign in. */
export class SignInNeeded extends ToolFailed {}

/** The sentences a tool answers when it holds no usable credential. */
const SIGN_IN_ANSWERS = [
    'not authenticated',
    'run the `login` tool',
    'sign in again with the login tool',
    'control plane rejected the token',
];

interface ToolResult {
    content?: { type: string; text?: string }[];
    isError?: boolean;
}

/** The text of a `tools/call` result. A tool reports a failure either way MCP allows: as `isError`, or as text that says
 * `Error:`, which is how the proxy tools pass on what the control plane answered. */
export function textOf(tool: string, result: unknown): string {
    const answer = result as ToolResult;
    const text = (answer.content ?? [])
        .filter((block) => block.type === 'text' && block.text !== undefined)
        .map((block) => block.text)
        .join('\n');
    if (SIGN_IN_ANSWERS.some((sentence) => text.toLowerCase().includes(sentence.toLowerCase()))) {
        throw new SignInNeeded(tool, text.replace(/^Error:\s*/, ''));
    }
    if (answer.isError || text.startsWith('Error:')) {
        throw new ToolFailed(tool, text.replace(/^Error:\s*/, ''));
    }
    return text;
}

/** A tool's answer read as the JSON it is. Text that is not JSON says why there is no answer, so it is the failure. */
export function jsonOf<T>(tool: string, text: string): T {
    try {
        return JSON.parse(text) as T;
    } catch {
        throw new ToolFailed(tool, text);
    }
}

/** What `login` answers: where the person approves the sign-in, and the code they compare there. */
export interface SignInRequest {
    url: string;
    code: string;
}

export function parseSignIn(text: string): SignInRequest {
    const code = /enter the code:\s*(\S+)/.exec(text)?.[1];
    const url = /Direct link:\s*(\S+?)\)/.exec(text)?.[1] ?? /open:\s*(\S+)/.exec(text)?.[1];
    if (code === undefined || url === undefined) {
        throw new ToolFailed('login', text);
    }
    return { url, code };
}

/** How a pending sign-in stands. */
export type SignInProgress =
    | { state: 'signed-in'; who: string | undefined }
    | { state: 'waiting' }
    | { state: 'slow-down' }
    | { state: 'denied' }
    | { state: 'expired' }
    | { state: 'signed-out'; reason: string };

/** Reads `check_auth_status`, which answers in sentences because a model is its first reader. */
export function parseAuthStatus(text: string): SignInProgress {
    const authenticated = /^Authenticated as ([^,.]+(?:\.[^,.\s]+)*)/.exec(text);
    if (authenticated) {
        return { state: 'signed-in', who: authenticated[1] };
    }
    if (text.startsWith('Signed in')) {
        return { state: 'signed-in', who: undefined };
    }
    if (/still waiting/i.test(text)) {
        return { state: 'waiting' };
    }
    if (/polling too fast/i.test(text)) {
        return { state: 'slow-down' };
    }
    if (/denied/i.test(text)) {
        return { state: 'denied' };
    }
    if (/code expired/i.test(text)) {
        return { state: 'expired' };
    }
    return { state: 'signed-out', reason: text };
}
