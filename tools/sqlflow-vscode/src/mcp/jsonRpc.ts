/** How long a request waits for its answer. A tool that queues work answers at once, so only a stuck server reaches it. */
const DEFAULT_TIMEOUT_MS = 120_000;

/** The JSON-RPC error a server answered, with its code. */
export class RpcError extends Error {
    readonly code: number;

    constructor(code: number, message: string) {
        super(message);
        this.code = code;
    }
}

interface Pending {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
    timer: NodeJS.Timeout;
}

/**
 * The client side of JSON-RPC 2.0 over a line-delimited stream, which is what MCP's stdio transport is. It holds no
 * process: `write` sends one line, and the owner feeds what it reads into `receive`, so it runs the same over a child
 * process and in a test.
 */
export class JsonRpcClient {
    private readonly write: (line: string) => void;
    private readonly pending = new Map<number, Pending>();
    private buffer = '';
    private nextId = 1;
    private closedBecause: string | undefined;

    constructor(write: (line: string) => void) {
        this.write = write;
    }

    request(method: string, params?: unknown, timeoutMs: number = DEFAULT_TIMEOUT_MS): Promise<unknown> {
        if (this.closedBecause !== undefined) {
            return Promise.reject(new Error(this.closedBecause));
        }
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(`${method} had no answer in ${Math.round(timeoutMs / 1000)}s.`));
            }, timeoutMs);
            this.pending.set(id, { resolve, reject, timer });
            this.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
        });
    }

    notify(method: string, params?: unknown): void {
        if (this.closedBecause === undefined) {
            this.write(JSON.stringify({ jsonrpc: '2.0', method, params }));
        }
    }

    /** Takes what the stream gave, whole lines or parts of them. A line that is not JSON is not protocol: it is skipped. */
    receive(chunk: string): void {
        this.buffer += chunk;
        let end = this.buffer.indexOf('\n');
        while (end >= 0) {
            const line = this.buffer.slice(0, end).trim();
            this.buffer = this.buffer.slice(end + 1);
            if (line !== '') {
                this.dispatch(line);
            }
            end = this.buffer.indexOf('\n');
        }
    }

    /** Fails everything still waiting; every later request fails at once with the same reason. */
    close(reason: string): void {
        this.closedBecause = reason;
        for (const [id, pending] of this.pending) {
            clearTimeout(pending.timer);
            pending.reject(new Error(reason));
            this.pending.delete(id);
        }
    }

    private dispatch(line: string): void {
        let message: { id?: number | string; method?: string; result?: unknown; error?: { code: number; message: string } };
        try {
            message = JSON.parse(line);
        } catch {
            return;
        }

        if (message.method !== undefined) {
            // A request of the server's own. This client offers no capability, so all it answers is a ping.
            if (message.id !== undefined) {
                this.write(JSON.stringify(
                    message.method === 'ping'
                        ? { jsonrpc: '2.0', id: message.id, result: {} }
                        : { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: `${message.method} is not offered.` } }
                ));
            }
            return;
        }

        const pending = typeof message.id === 'number' ? this.pending.get(message.id) : undefined;
        if (pending === undefined) {
            return;
        }
        this.pending.delete(message.id as number);
        clearTimeout(pending.timer);
        if (message.error) {
            pending.reject(new RpcError(message.error.code, message.error.message));
        } else {
            pending.resolve(message.result);
        }
    }
}
