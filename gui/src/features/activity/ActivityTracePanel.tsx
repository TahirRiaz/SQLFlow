import { useMemo } from "react";
import { toast } from "sonner";
import { TraceLog, type TraceLine } from "@/components/TraceLog";
import { parseUtc } from "../../lib/time";
import type { ActivityEvent } from "../../api/types";
import { useActivityTraceStream } from "./useActivityTraceStream";

/** The whole trace as one plain-text block, for Copy (the panel's terminal-log layout). */
function traceToText(entries: ActivityEvent[]): string {
  return entries
    .map((e) => {
      const time = parseUtc(e.timestampUtc).toISOString().slice(11, 23);
      return `${time}  ${(e.step ?? e.level).toUpperCase().padEnd(10)} ${e.message ?? ""}`;
    })
    .join("\n");
}

/**
 * The bottom-panel trace view for one control-plane operation (a repository sync, a lineage computation, ...):
 * subscribes to its live activity trace for (kind, subject) and renders it through the shared {@link TraceLog}, so
 * it looks identical to the pipeline run trace. Bumping `nonce` restarts the stream (a repeat trigger). Rendered
 * inside the workbench PanelHost via usePanel().
 *
 * The stream replays the subject's last few attempts as scrollback before the live one, but the header reports the
 * newest attempt alone, the way a run's trace reports the run's own status: an earlier failure stays readable above
 * a later success without turning the panel red. Lines arrive in id order and the stream ends only once the newest
 * line is terminal, so the last line held is the newest attempt's, and at the end it carries that attempt's outcome.
 */
export function ActivityTracePanel({
  kind,
  subject,
  nonce,
  onEnded,
}: {
  kind: string;
  subject: string;
  nonce: number;
  onEnded?: () => void;
}) {
  const { entries, connected, ended } = useActivityTraceStream(kind, subject, nonce, onEnded);

  const lines = useMemo<TraceLine[]>(
    () => entries.map((e) => ({
      key: String(e.id),
      timestampUtc: e.timestampUtc,
      tag: e.step ?? e.level,
      level: e.level,
      message: e.message ?? "",
      groupKey: e.activityId,
    })),
    [entries],
  );

  const latest = entries.length > 0 ? entries[entries.length - 1] : undefined;
  const failed = latest !== undefined && latest.terminal && latest.status === "failed";

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(traceToText(entries));
      toast.success("Trace copied");
    } catch {
      toast.error("Could not copy the trace");
    }
  };

  return (
    <TraceLog
      lines={lines}
      connected={connected}
      ended={ended}
      failed={failed}
      currentGroup={latest?.activityId}
      onCopy={copy}
      emptyLive="Waiting for the operation to start…"
      emptyEnded="No trace to show."
    />
  );
}
