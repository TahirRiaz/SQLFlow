import { useEffect, useRef, useState } from "react";
import { runApi } from "../../api/endpoints";
import type { RunSummary } from "../../api/types";

/** The members in display order: the lineage step (the Step column), then flow name, then run id. */
function compareMembers(a: RunSummary, b: RunSummary): number {
  if (a.wave !== b.wave) {
    return a.wave - b.wave;
  }

  if (a.flowName !== b.flowName) {
    return a.flowName < b.flowName ? -1 : 1;
  }

  return a.runId < b.runId ? -1 : a.runId > b.runId ? 1 : 0;
}

export interface LiveRunGroup {
  /** Every member's latest summary, in display order; empty until the connect snapshot lands. */
  members: RunSummary[];
  /** True while the SSE connection is open; false during a reconnect backoff. */
  connected: boolean;
}

/**
 * Subscribes to a run group's live stream while `live` is true: each `member` frame replaces that member's
 * summary (status transitions, the newest trace event as its "last action", rows, timing), the server sends a
 * full snapshot on connect so a reconnect self-heals, and the terminal `end` frame fires `onEnded` exactly once
 * so the page can refetch the authoritative at-rest views. The subscription closes when `live` turns false or
 * the component unmounts.
 */
export function useRunGroupStream(groupId: string, live: boolean, onEnded: () => void): LiveRunGroup {
  const [members, setMembers] = useState<RunSummary[]>([]);
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
    const byRunId = new Map<string, RunSummary>();
    setMembers([]);

    const run = async () => {
      while (!disposed) {
        let ended = false;
        try {
          await runApi.streamGroup(groupId, (frame) => {
            if (frame.event === "member") {
              const member = JSON.parse(frame.data) as RunSummary;
              byRunId.set(member.runId, member);
              setMembers([...byRunId.values()].sort(compareMembers));
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

        // The server closed without an end frame (a control plane restart, a proxy timeout): reconnect; the
        // fresh snapshot replaces the map, so nothing is lost and nothing stale survives.
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
  }, [groupId, live]);

  return { members, connected };
}
