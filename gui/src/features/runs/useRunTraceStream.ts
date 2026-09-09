import { useEffect, useRef, useState } from "react";
import { runApi } from "../../api/endpoints";
import type { RunTraceEntry } from "../../api/types";

/** Orders live entries exactly like the paged endpoint: timestamp, then per-stream ordinal, kind, id. ISO-8601
 * UTC strings compare correctly as strings, and entries without a timestamp (legacy statements) sort first. */
function compareEntries(a: RunTraceEntry, b: RunTraceEntry): number {
  const ta = a.timestampUtc ?? "";
  const tb = b.timestampUtc ?? "";
  if (ta !== tb) {
    return ta < tb ? -1 : 1;
  }

  if (a.ordinal !== b.ordinal) {
    return a.ordinal - b.ordinal;
  }

  if (a.kind !== b.kind) {
    return a.kind < b.kind ? -1 : 1;
  }

  return a.id - b.id;
}

export interface LiveRunTrace {
  /** Every entry received so far, in timeline order. Resets when a new stream opens. */
  entries: RunTraceEntry[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
}

/**
 * Subscribes to a run's live trace stream while `live` is true: entries accumulate in timeline order as the
 * executing node persists them, and a dropped connection reconnects after two seconds resuming from per-stream
 * id cursors (no replay, no gaps). The server's tail is an append-only log keyed by a stable id, so every entry
 * arrives exactly once and there is no client-side de-duplication; the entries are only sorted for a stable
 * timeline. When the server sends the terminal `end` frame, `onEnded` fires exactly once so the page can refetch
 * the authoritative at-rest timeline. The subscription closes when `live` turns false or the component unmounts.
 */
export function useRunTraceStream(runId: string, live: boolean, onEnded: () => void): LiveRunTrace {
  const [entries, setEntries] = useState<RunTraceEntry[]>([]);
  const [connected, setConnected] = useState(false);
  // The latest callback without re-subscribing the stream when the parent re-renders.
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  useEffect(() => {
    if (!live) {
      return undefined;
    }

    let disposed = false;
    const controller = new AbortController();
    const cursors = { afterEventId: 0, afterStatementId: 0 };
    setEntries([]);

    const run = async () => {
      while (!disposed) {
        let ended = false;
        try {
          await runApi.streamTrace(runId, cursors, (frame) => {
            if (frame.event === "entry") {
              const entry = JSON.parse(frame.data) as RunTraceEntry;
              // The tail never re-sends a row and the cursors resume past what we hold, so append unconditionally;
              // the sort only keeps the two interleaved streams in timeline order.
              if (entry.kind === "event") {
                cursors.afterEventId = Math.max(cursors.afterEventId, entry.id);
              } else {
                cursors.afterStatementId = Math.max(cursors.afterStatementId, entry.id);
              }

              setEntries((previous) => [...previous, entry].sort(compareEntries));
            } else if (frame.event === "end") {
              ended = true;
            }
          }, controller.signal, () => setConnected(true));
        } catch (error) {
          if (disposed || (error instanceof DOMException && error.name === "AbortError")) {
            return;
          }
          // A failed handshake or a mid-stream network error: fall through to the reconnect backoff.
        }

        if (ended) {
          if (!disposed) {
            onEndedRef.current();
          }

          return;
        }

        // The server closed without an end frame (a control plane restart, a proxy timeout): reconnect from
        // the cursors so nothing replays and nothing is lost.
        setConnected(false);
        await new Promise<void>((resolve) => {
          window.setTimeout(resolve, 2000);
        });
      }
    };

    void run();
    return () => {
      disposed = true;
      controller.abort();
      setConnected(false);
    };
  }, [runId, live]);

  return { entries, connected };
}
