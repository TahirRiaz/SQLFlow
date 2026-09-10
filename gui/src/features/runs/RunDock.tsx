import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CircleCheck, CircleX, ExternalLink, Loader2, Minus, X } from "lucide-react";
import { cn } from "@/lib/utils";
import type { RunGroup } from "../../api/types";
import { runApi } from "../../api/endpoints";
import { pollingInterval } from "../../hooks/usePolling";
import { useRunDock } from "./RunDockContext";

/** A finished chip lingers this long so the operator registers the outcome, then removes itself rather than piling
 * up in the tray. A chip that is still executing never expires; the user can dismiss any chip by hand. */
const FINISHED_LINGER_MS = 45_000;

interface GroupRollup {
  total: number;
  done: number;
  failed: number;
  active: boolean;
}

/** Reduces a group's member counts to what the chip draws: how many are terminal, how many failed, and whether any
 * member is still queued or running. */
function rollup(group: RunGroup): GroupRollup {
  const { counts } = group;
  const done = counts.succeeded + counts.failed + counts.cancelled + counts.skipped;
  return {
    total: counts.total,
    done,
    failed: counts.failed,
    active: counts.queued > 0 || counts.running > 0,
  };
}

/**
 * One tracked run group as a compact card: its anchor (the batch label or flow name), a live progress bar, and a
 * done/total tally. It shares the run-group query cache with the full group page, so opening the board from a chip is
 * instant and already warm. A finished group lingers briefly and then removes itself; a group the server no longer
 * knows (deleted, or an id from a wiped estate) is dropped at once.
 */
function RunDockChip({ groupId }: { groupId: string }) {
  const navigate = useNavigate();
  const { untrack } = useRunDock();

  const query = useQuery({
    queryKey: ["run-groups", groupId],
    queryFn: () => runApi.group(groupId),
    // Poll while any member is queued or running; go quiet once the whole group is terminal.
    refetchInterval: (q) => {
      const data = q.state.data;
      if (data && data.counts.queued === 0 && data.counts.running === 0) {
        return false;
      }

      return pollingInterval(3000)();
    },
  });

  // A group the API cannot resolve is not worth tracking; drop it so a stale localStorage id cannot wedge the dock.
  const notFound = query.isError;
  useEffect(() => {
    if (notFound) {
      untrack(groupId);
    }
  }, [notFound, groupId, untrack]);

  const group = query.data;
  const stats = group ? rollup(group) : null;

  // Once the group is terminal, start the linger timer; if it somehow goes active again the timer is cleared.
  const finished = stats !== null && !stats.active;
  useEffect(() => {
    if (!finished) {
      return undefined;
    }

    const handle = window.setTimeout(() => untrack(groupId), FINISHED_LINGER_MS);
    return () => window.clearTimeout(handle);
  }, [finished, groupId, untrack]);

  if (group === undefined || stats === null) {
    return null;
  }

  const progress = stats.total > 0 ? Math.round((stats.done / stats.total) * 100) : 0;
  const tone = stats.failed > 0
    ? { text: "text-destructive", bar: "bg-destructive", border: "border-destructive/40" }
    : stats.active
      ? { text: "text-info", bar: "bg-info", border: "border-info/40" }
      : { text: "text-success", bar: "bg-success", border: "border-success/40" };
  const modeLabel = group.mode === "node" ? "flow + descendants" : "batch";

  return (
    <div
      className={cn("group/chip flex items-stretch overflow-hidden rounded-md border bg-card shadow-sm", tone.border)}
      data-testid="run-dock-chip"
    >
      <button
        type="button"
        onClick={() => navigate(`/runs/groups/${groupId}`)}
        className="flex min-w-0 flex-1 items-center gap-2.5 px-2.5 py-2 text-left hover:bg-accent/50"
        title="Open the run"
      >
        <span className={cn("flex size-6 shrink-0 items-center justify-center", tone.text)}>
          {stats.active
            ? <Loader2 className="size-4 animate-spin" />
            : stats.failed > 0
              ? <CircleX className="size-4" />
              : <CircleCheck className="size-4" />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1.5">
            <span className="truncate font-mono text-[12px] font-medium">{group.anchor}</span>
            <span className="shrink-0 text-[10px] uppercase tracking-wider text-muted-foreground">{modeLabel}</span>
          </span>
          <span className="mt-1 flex items-center gap-2">
            <span className="h-1 flex-1 overflow-hidden rounded-full bg-muted">
              <span
                className={cn("block h-full transition-all duration-500", tone.bar)}
                style={{ width: `${progress}%` }}
              />
            </span>
            <span className="shrink-0 font-mono text-[10px] tabular-nums text-muted-foreground">
              {stats.done}/{stats.total}
              {stats.failed > 0 ? ` · ${stats.failed} failed` : ""}
            </span>
          </span>
        </span>
        <ExternalLink className="size-3.5 shrink-0 text-muted-foreground opacity-0 transition-opacity group-hover/chip:opacity-100" />
      </button>
      <button
        type="button"
        onClick={() => untrack(groupId)}
        className="flex shrink-0 items-center px-1.5 text-muted-foreground hover:bg-accent/50 hover:text-foreground"
        aria-label="Dismiss from the run tray"
        data-testid="run-dock-dismiss"
      >
        <X className="size-3.5" />
      </button>
    </div>
  );
}

/**
 * The run tray: a globally-mounted, always-on-top dock that lists the run groups the operator launched this session
 * so a running execution is never lost behind a closed sheet or a tab change. It floats above the workbench (below
 * modals), can be minimized to a single pill and reopened, and each chip links to the full, cancellable run board.
 * State (which groups, minimized-or-not) lives in RunDockContext and is persisted, so the tray survives a reload.
 */
export function RunDock() {
  const { groupIds, minimized, setMinimized } = useRunDock();

  if (groupIds.length === 0) {
    return null;
  }

  if (minimized) {
    return (
      <button
        type="button"
        onClick={() => setMinimized(false)}
        className="fixed right-3 bottom-8 z-40 flex items-center gap-2 rounded-full border bg-card px-3 py-1.5 text-[12px] font-medium shadow-md hover:bg-accent/50"
        data-testid="run-dock-pill"
      >
        <Loader2 className="size-3.5 animate-spin text-info" />
        {groupIds.length} {groupIds.length === 1 ? "run" : "runs"}
      </button>
    );
  }

  return (
    <div
      className="fixed right-3 bottom-8 z-40 flex w-[340px] max-w-[calc(100vw-1.5rem)] flex-col gap-2 rounded-lg border bg-card/95 p-2 shadow-lg backdrop-blur-sm"
      data-testid="run-dock"
    >
      <div className="flex items-center gap-2 px-1">
        <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Runs</span>
        <span className="text-[11px] text-muted-foreground/70">{groupIds.length}</span>
        <span className="flex-1" />
        <button
          type="button"
          onClick={() => setMinimized(true)}
          className="flex size-5 items-center justify-center rounded text-muted-foreground hover:bg-accent/50 hover:text-foreground"
          aria-label="Minimize the run tray"
          data-testid="run-dock-minimize"
        >
          <Minus className="size-3.5" />
        </button>
      </div>
      <div className="flex flex-col gap-1.5">
        {/* Newest on top: the run just launched is the one the operator is looking for. */}
        {[...groupIds].reverse().map((id) => <RunDockChip key={id} groupId={id} />)}
      </div>
    </div>
  );
}
