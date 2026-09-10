import { runtimeConfig } from "../config/runtime";

/** An RFC 7807 problem from the API, with the correlation id support needs to find the server log line. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  readonly correlationId: string | null;

  constructor(status: number, title: string, detail: string | null, correlationId: string | null) {
    super(detail ? `${title}: ${detail}` : title);
    this.name = "ApiError";
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.correlationId = correlationId;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

// ---- Auth token wiring (owned by AuthContext) ---------------------------------------------------------------------

let currentToken: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function setAuthToken(token: string | null): void {
  currentToken = token;
}

/** AuthContext registers the session-expiry handler: called when an authenticated call answers 401. */
export function setUnauthorizedHandler(handler: (() => void) | null): void {
  onUnauthorized = handler;
}

// ---- Rate-limit pause (the 120/min budget) --------------------------------------------------------------------------

type RateLimitListener = (pausedUntilMs: number | null) => void;

let pausedUntilMs: number | null = null;
const rateLimitListeners = new Set<RateLimitListener>();

/** Polling queries consult this: while paused, refetch intervals back off so the budget can recover. */
export function rateLimitPausedUntil(): number | null {
  if (pausedUntilMs !== null && Date.now() >= pausedUntilMs) {
    pausedUntilMs = null;
  }

  return pausedUntilMs;
}

export function subscribeRateLimit(listener: RateLimitListener): () => void {
  rateLimitListeners.add(listener);
  return () => rateLimitListeners.delete(listener);
}

function pauseForRateLimit(retryAfterSeconds: number | null): void {
  const pauseMs = (retryAfterSeconds ?? 30) * 1000;
  pausedUntilMs = Date.now() + pauseMs;
  for (const listener of rateLimitListeners) {
    listener(pausedUntilMs);
  }
  window.setTimeout(() => {
    if (pausedUntilMs !== null && Date.now() >= pausedUntilMs) {
      pausedUntilMs = null;
      for (const listener of rateLimitListeners) {
        listener(null);
      }
    }
  }, pauseMs + 50);
}

// ---- Requests ------------------------------------------------------------------------------------------------------------

export type QueryParams = Record<string, string | number | boolean | null | undefined>;

interface RequestOptions {
  method: string;
  path: string;
  query?: QueryParams;
  body?: unknown;
  signal?: AbortSignal;
  /** Sign-in calls set this: a 401 is the expected "wrong credentials" answer, not an expired session. */
  anonymous?: boolean;
}

async function request<T>(options: RequestOptions): Promise<T> {
  const base = runtimeConfig().apiBaseUrl;
  const url = new URL(`${base}${options.path}`);
  if (options.query) {
    for (const [key, value] of Object.entries(options.query)) {
      if (value !== null && value !== undefined && value !== "") {
        url.searchParams.set(key, String(value));
      }
    }
  }

  const headers: Record<string, string> = { Accept: "application/json" };
  if (!options.anonymous && currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method,
      headers,
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      signal: options.signal ?? null,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      throw error;
    }

    throw new ApiError(0, "Control plane unreachable",
      "The API did not answer; check that the control plane is running and CORS allows this origin.", null);
  }

  if (response.status === 401 && !options.anonymous && currentToken) {
    onUnauthorized?.();
  }

  if (response.status === 429) {
    const retryAfter = response.headers.get("Retry-After");
    pauseForRateLimit(retryAfter ? Number.parseInt(retryAfter, 10) || null : null);
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  // Action endpoints (restart/delete a node, and others) return 200 with an empty body. Read the payload as text and
  // only parse it when non-empty; calling response.json() on an empty body throws "Unexpected end of JSON input".
  const text = await response.text();
  return (text.length > 0 ? (JSON.parse(text) as T) : (undefined as T));
}

async function toApiError(response: Response): Promise<ApiError> {
  let title = `HTTP ${response.status}`;
  let detail: string | null = null;
  let correlationId = response.headers.get("X-Correlation-ID");
  const contentType = response.headers.get("Content-Type") ?? "";
  if (contentType.includes("json")) {
    try {
      const problem = (await response.json()) as {
        title?: string;
        detail?: string;
        correlationId?: string;
        extensions?: { correlationId?: string };
      };
      title = problem.title ?? title;
      detail = problem.detail ?? null;
      correlationId = problem.correlationId ?? problem.extensions?.correlationId ?? correlationId;
    } catch {
      // A non-JSON error body (proxy page, empty body): the status-derived title stands.
    }
  }

  return new ApiError(response.status, title, detail, correlationId);
}

export function get<T>(path: string, query?: QueryParams, signal?: AbortSignal): Promise<T> {
  return request<T>({ method: "GET", path, query, signal });
}

/** GET for a text/plain endpoint (the rendered run trace): same auth and error shaping, raw string body. */
export async function getText(path: string, signal?: AbortSignal): Promise<string> {
  const base = runtimeConfig().apiBaseUrl;
  const url = new URL(`${base}${path}`);
  const headers: Record<string, string> = { Accept: "text/plain" };
  if (currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }

  const response = await fetch(url, { method: "GET", headers, signal: signal ?? null });
  if (response.status === 401 && currentToken) {
    onUnauthorized?.();
  }

  if (response.status === 429) {
    const retryAfter = response.headers.get("Retry-After");
    pauseForRateLimit(retryAfter ? Number.parseInt(retryAfter, 10) || null : null);
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  return response.text();
}

// ---- Server-Sent Events ---------------------------------------------------------------------------------------------

/** One parsed SSE frame: the event name (default "message") and the joined data payload. */
export interface SseFrame {
  event: string;
  data: string;
}

/**
 * Opens an authenticated SSE stream and invokes `onFrame` for every frame until the server closes it or the
 * signal aborts. Built on fetch (not EventSource) so the bearer token travels in the Authorization header like
 * every other call; the caller owns reconnect policy. Heartbeat comments are consumed silently. Resolves when
 * the server ends the stream; rejects with an AbortError on cancellation and an ApiError on a failed handshake.
 * An action stream (the chat assistant's answer) POSTs a JSON body via `init`; plain tails default to GET.
 */
export async function streamSse(
  path: string,
  query: QueryParams | undefined,
  onFrame: (frame: SseFrame) => void,
  signal: AbortSignal,
  onOpen?: () => void,
  init?: { method?: "GET" | "POST"; body?: unknown },
): Promise<void> {
  const base = runtimeConfig().apiBaseUrl;
  const url = new URL(`${base}${path}`);
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== "") {
        url.searchParams.set(key, String(value));
      }
    }
  }

  const headers: Record<string, string> = { Accept: "text/event-stream" };
  if (currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }
  if (init?.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const response = await fetch(url, {
    method: init?.method ?? "GET",
    headers,
    body: init?.body !== undefined ? JSON.stringify(init.body) : undefined,
    signal,
  });
  if (response.status === 401 && currentToken) {
    onUnauthorized?.();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  if (!response.body) {
    throw new ApiError(0, "Streaming unsupported", "The response exposes no readable body.", null);
  }

  // The handshake succeeded and the body is readable: the stream is genuinely open (a "live" indicator
  // flipped here is honest, unlike one set before the request).
  onOpen?.();

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      return;
    }

    // The server writes LF; normalizing CRLF too keeps the parser correct behind any proxy that rewrites lines.
    buffer += decoder.decode(value, { stream: true }).replaceAll("\r\n", "\n");
    for (;;) {
      const separator = buffer.indexOf("\n\n");
      if (separator < 0) {
        break;
      }

      const frame = parseSseFrame(buffer.slice(0, separator));
      buffer = buffer.slice(separator + 2);
      if (frame !== null) {
        onFrame(frame);
      }
    }
  }
}

/** Parses one raw SSE frame; returns null for comment-only frames (heartbeats). */
function parseSseFrame(raw: string): SseFrame | null {
  let event = "message";
  const data: string[] = [];
  for (const line of raw.split("\n")) {
    if (line.startsWith(":")) {
      continue;
    }

    if (line.startsWith("event:")) {
      event = line.slice("event:".length).trim();
    } else if (line.startsWith("data:")) {
      data.push(line.slice("data:".length).trimStart());
    }
  }

  return data.length === 0 ? null : { event, data: data.join("\n") };
}

export function post<T>(path: string, body?: unknown): Promise<T> {
  return request<T>({ method: "POST", path, body });
}

/**
 * POST for a raw binary body (the chat voice recording): same auth and error shaping as every other
 * call, but the payload travels as-is under its own content type instead of JSON.
 */
export async function postBinary<T>(
  path: string,
  body: Blob,
  contentType: string,
  query?: QueryParams,
  signal?: AbortSignal,
): Promise<T> {
  const base = runtimeConfig().apiBaseUrl;
  const url = new URL(`${base}${path}`);
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== "") {
        url.searchParams.set(key, String(value));
      }
    }
  }

  const headers: Record<string, string> = { Accept: "application/json", "Content-Type": contentType };
  if (currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }

  const response = await fetch(url, { method: "POST", headers, body, signal: signal ?? null });
  if (response.status === 401 && currentToken) {
    onUnauthorized?.();
  }
  if (response.status === 429) {
    const retryAfter = response.headers.get("Retry-After");
    pauseForRateLimit(retryAfter ? Number.parseInt(retryAfter, 10) || null : null);
  }
  if (!response.ok) {
    throw await toApiError(response);
  }

  const text = await response.text();
  return (text.length > 0 ? (JSON.parse(text) as T) : (undefined as T));
}

export function put<T>(path: string, body?: unknown): Promise<T> {
  return request<T>({ method: "PUT", path, body });
}

export function del<T>(path: string): Promise<T> {
  return request<T>({ method: "DELETE", path });
}

export function postAnonymous<T>(path: string, body: unknown): Promise<T> {
  return request<T>({ method: "POST", path, body, anonymous: true });
}

export function getAnonymous<T>(path: string): Promise<T> {
  return request<T>({ method: "GET", path, anonymous: true });
}
