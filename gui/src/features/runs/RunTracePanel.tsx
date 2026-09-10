import { useCallback, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { TraceLog, type TraceLine } from "@/components/TraceLog";
import { runApi } from "../../api/endpoints";
import type { RunTraceEntry } from "../../api/types";
import { pollingInterval } from "../../hooks/usePolling";
import { useRunTraceStream } from "./useRunTraceStream";

/** The most trace entries a run's panel loads at rest: a run's trace is bounded, and "Copy trace" always fetches
 * the complete server-rendered document, so this cap only bounds the on-screen scrollback. */
const AT_REST_CAP = 2000;

function toLine(entry: RunTraceEntry): TraceLine {
  const isStatement = entry.kind === "statement";
  return {
    key: `${entry.kind}-${entry.id}`,
    timestampUtc: entry.timestampUtc,
    tag: entry.step ?? entry.kind,
    // A statement row's level is always "trace"; event rows carry their own level.
    level: isStatement ? "trace" : entry.level,
    // A statement row is its SQL (shown as the event); no redundant "statement" label. A failure is carried by
    // the error field, so it still stands out. Event rows carry their own message.
    message: isStatement ? "" : entry.message ?? "",
    sql: entry.sql,
    error: entry.error,
  };
}

/**
 * The bottom-panel trace view for a pipeline run: self-contained given a run id, so the workbench panel keeps it
 * alive independent of the run detail page. While the run is queued/running it streams the live trace (SSE, the
 * moment the executing node persists each entry); once terminal it shows the authoritative at-rest trace. Rendered
 * through the shared {@link TraceLog}, so it looks identical to the repository sync trace. Copy fetches the same
 * canonical plain-text document the LLM-facing /trace/text endpoint serves.
 */
export function RunTracePanel({ runId }: { runId: string }) {
  const queryClient = useQueryClient();

  // The run header drives live vs at-rest; shares the ["runs", runId] cache with the run detail page.
  const runQuery = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId),
    refetchInterval: (q) => {
      const status = q.state.data?.status;
      if (status === "succeeded" || status === "failed" || status === "cancelled") {
        return false;
      }

      return pollingInterval(3000)();
    },
  });

  const run = runQuery.data;
  const live = run?.status === "queued" || run?.status === "running";

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["runs", runId] });
  }, [queryClient, runId]);

  const { entries: liveEntries, connected } = useRunTraceStream(runId, live, refresh);

  // At rest (or before the run has loaded), the authoritative trace comes from the paged endpoint.
  const atRestQuery = useQuery({
    queryKey: ["runs", runId, "trace", "panel"],
    queryFn: () => runApi.trace(runId, { page: 1, pageSize: AT_REST_CAP }),
    enabled: run !== undefined && !live,
  });

  const lines = useMemo<TraceLine[]>(
    () => (live ? liveEntries : atRestQuery.data?.items ?? []).map(toLine),
    [live, liveEntries, atRestQuery.data],
  );

  const ended = run !== undefined && !live;
  const failed = run?.status === "failed";

  const copy = async () => {
    try {
      const text = await runApi.traceText(runId);
      await navigator.clipboard.writeText(text);
      toast.success("Trace copied to clipboard.");
    } catch {
      toast.error("Could not copy the trace.");
    }
  };

  return (
    <TraceLog
      lines={lines}
      connected={connected}
      ended={ended}
      failed={failed}
      onCopy={copy}
      emptyLive={run?.status === "queued"
        ? "The run is queued; the trace streams in once a node claims it."
        : "Waiting for the first trace entry…"}
      emptyEnded="No trace was recorded for this run."
    />
  );
}
