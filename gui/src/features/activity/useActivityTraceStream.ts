import { useEffect, useRef, useState } from "react";
import { activityApi } from "../../api/endpoints";
import type { ActivityEvent } from "../../api/types";

export interface LiveActivityTrace {
  /** Every line received so far, in id (timeline) order. Resets when the stream re-opens for a new nonce. */
  entries: ActivityEvent[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
  /** True once the server sent the terminal `end` frame for the current activity. */
  ended: boolean;
}

/**
 * Subscribes to one operation's live activity trace for a (kind, subject) pair: lines accumulate in id order as the
 * operation persists them, and a dropped connection reconnects after two seconds resuming from the last id (no
 * replay, no gaps). The append-only log is keyed by a stable, monotonic id, so every line arrives exactly once and
 * there is no client-side de-duplication. When the server sends the terminal `end` frame `onEnded` fires once (so a
 * page can refresh at-rest state) and the stream stops reconnecting. Bumping `nonce` re-opens a fresh stream from
 * the start, which is how a repeat trigger (a new "Sync now") restarts a completed trace. The subscription closes
 * when the component unmounts.
 *
 * This is the general-purpose twin of useRunTraceStream: the same tail-an-append-only-log mechanism, keyed by a
 * kind + subject instead of a run id, so any traced operation reuses it.
 */
export function useActivityTraceStream(
  kind: string,
  subject: string,
  nonce: number,
  onEnded?: () => void,
): LiveActivityTrace {
  const [entries, setEntries] = useState<ActivityEvent[]>([]);
  const [connected, setConnected] = useState(false);
  const [ended, setEnded] = useState(false);
  // The latest callback without re-subscribing the stream when the parent re-renders.
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();
    const cursor = { afterId: 0 };
    setEntries([]);
    setEnded(false);

    const run = async () => {
      while (!disposed) {
        let endedNow = false;
        try {
          await activityApi.streamTrace(kind, subject, cursor, (frame) => {
            if (frame.event === "entry") {
              const entry = JSON.parse(frame.data) as ActivityEvent;
              // The tail never re-sends a row and the cursor resumes past what we hold, so append unconditionally.
              cursor.afterId = Math.max(cursor.afterId, entry.id);
              setEntries((previous) => [...previous, entry]);
            } else if (frame.event === "end") {
              endedNow = true;
            }
          }, controller.signal, () => setConnected(true));
        } catch (error) {
          if (disposed || (error instanceof DOMException && error.name === "AbortError")) {
            return;
          }
          // A failed handshake or a mid-stream network error: fall through to the reconnect backoff.
        }

        if (endedNow) {
          if (!disposed) {
            setEnded(true);
            onEndedRef.current?.();
          }

          return;
        }

        // The server closed without an end frame (a control plane restart, a proxy timeout): reconnect from the
        // cursor so nothing replays and nothing is lost.
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
  }, [kind, subject, nonce]);

  return { entries, connected, ended };
}
