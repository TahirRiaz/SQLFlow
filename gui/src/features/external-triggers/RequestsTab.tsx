import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowRight, CircleAlert, CircleCheck, CircleMinus, CircleStop, ClipboardCopy, Loader2 } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { triggerRequestApi } from "../../api/endpoints";
import type { ExternalTrigger, TriggerRequest } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { FilterBar } from "../../components/FilterBar";
import { IconAction } from "../../components/IconAction";
import { LinkRef } from "../../components/LinkRef";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { TruncatedText } from "../../components/TruncatedText";
import { parseUtc } from "../../lib/time";
import { RequestStateBadge, ScheduleChip, errorText, shortSha, syncText } from "./triggerVisuals";

const STATES = ["syncing", "firing", "running", "succeeded", "failed", "cancelled"] as const;
const ALL = "all";

/** How long a request took, or has been going: from when it was made to when it ended (or now). */
export function tookText(request: Pick<TriggerRequest, "createdUtc" | "completedUtc">, nowMs: number): string {
  const end = request.completedUtc === null ? nowMs : parseUtc(request.completedUtc).getTime();
  const seconds = Math.max(0, Math.round((end - parseUtc(request.createdUtc).getTime()) / 1000));
  if (seconds < 60) {
    return `${seconds} s`;
  }

  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes}m ${String(seconds % 60).padStart(2, "0")}s` : `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`;
}

/** A request's sync in one cell: a glyph for how it went and the commit (asked for, then synced at when they differ),
 * with both commits in full and the sync's error on hover. */
function SyncCell({ request }: { request: TriggerRequest }) {
  const outcome = request.syncOutcome;
  const { icon: Icon, tone, spin } = outcome === "succeeded" || outcome === "current"
    ? { icon: CircleCheck, tone: "text-success", spin: false }
    : outcome === "failed" || outcome === "timed-out"
      ? { icon: CircleAlert, tone: "text-destructive", spin: false }
      : outcome === "skipped"
        ? { icon: CircleMinus, tone: "text-muted-foreground", spin: false }
        : request.conclusion === null
          ? { icon: Loader2, tone: "text-info", spin: true }
          : { icon: CircleMinus, tone: "text-muted-foreground", spin: false };
  const moved = request.expectedCommit !== null && request.syncedCommit !== null
    && request.syncedCommit.toLowerCase() !== request.expectedCommit;
  const body = [
    `Sync: ${syncText(request)}`,
    `Asked for: ${request.expectedCommit ?? (request.sync ? "whatever the sync pulls" : "no sync")}`,
    `Synced at: ${request.syncedCommit ?? "not yet"}`,
    ...(request.syncError ? ["", request.syncError] : []),
  ].join("\n");
  return (
    <RichTooltip title="Repository sync" body={body} mono align="start">
      <span className="inline-flex items-center gap-1.5 font-mono text-[12px]" data-testid="request-sync" data-outcome={outcome ?? "pending"}>
        <Icon className={`size-3.5 shrink-0 ${tone}${spin ? " animate-spin" : ""}`} aria-hidden />
        <span className="sr-only">{syncText(request)}</span>
        {outcome === "failed" || outcome === "timed-out"
          ? <span>{syncText(request)}</span>
          : (
            <>
              <span>{shortSha(request.expectedCommit ?? request.syncedCommit) || "-"}</span>
              {moved && (
                <>
                  <ArrowRight className="size-3 text-muted-foreground" aria-hidden />
                  <span>{shortSha(request.syncedCommit)}</span>
                </>
              )}
            </>
          )}
      </span>
    </RichTooltip>
  );
}

/** The schedules a request asked for, on one line: the first two as chips, and how many more, named on hover. */
function SchedulesCell({ request }: { request: TriggerRequest }) {
  if (request.schedules.length === 0) {
    return <span className="text-[13px] text-muted-foreground">none: sync only</span>;
  }

  const shown = request.schedules.slice(0, 2);
  const rest = request.schedules.slice(2);
  return (
    <div className="flex items-center gap-1 overflow-hidden">
      {shown.map((s) => <ScheduleChip key={s.ordinal} schedule={s} />)}
      {rest.length > 0 && (
        <RichTooltip title="Also" body={rest.map((s) => `${s.name}: ${s.state}`).join("\n")} mono>
          <span className="shrink-0 text-[12px] text-muted-foreground">+{rest.length}</span>
        </RichTooltip>
      )}
    </div>
  );
}

/**
 * Every request the triggers (and people) made, newest first: where it stands, who asked and from which job, the commit
 * asked for and the one synced, how the sync went, each schedule it asked for with how it went, and how long it took. A
 * row opens the request's live trace in the bottom panel; its actions copy the summary a GitHub job writes, or cancel a
 * request still running. The list refreshes while any request runs.
 */
export function RequestsTab({
  triggers, triggerId, onTriggerChange, onOpen,
}: {
  triggers: ExternalTrigger[] | undefined;
  triggerId: string | null;
  onTriggerChange: (triggerId: string | null) => void;
  onOpen: (request: TriggerRequest) => void;
}) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<string>(ALL);
  const [cancelTarget, setCancelTarget] = useState<TriggerRequest | null>(null);
  const [running, setRunning] = useState(false);
  // A Repository column that says the same on every row says nothing: it shows only when the page spans repositories.
  const [singleRepo, setSingleRepo] = useState(true);

  const copySummary = useMutation({
    mutationFn: async (request: TriggerRequest) => {
      const markdown = await triggerRequestApi.summary(request.id);
      await navigator.clipboard.writeText(markdown);
    },
    onSuccess: () => toast.success("Summary copied as Markdown."),
    onError: (error) => toast.error(errorText(error)),
  });

  const cancel = useMutation({
    mutationFn: (request: TriggerRequest) => triggerRequestApi.cancel(request.id),
    onSuccess: () => {
      toast.success("Cancel asked for: the request stops, and its queued runs are cancelled.");
      setCancelTarget(null);
      void queryClient.invalidateQueries({ queryKey: ["trigger-requests"] });
    },
    onError: (error) => {
      toast.error(errorText(error));
      setCancelTarget(null);
    },
  });

  const nowMs = Date.now();
  const columns: Column<TriggerRequest>[] = [
    {
      id: "state",
      header: "Took",
      render: (r) => (
        <span className="inline-flex items-center gap-1.5">
          <RequestStateBadge state={r.state} />
          <span className="font-mono text-[12px] tabular-nums text-muted-foreground">{tookText(r, nowMs)}</span>
        </span>
      ),
    },
    { id: "when", header: "Asked", render: (r) => <span className="text-[12px]"><RelativeTime value={r.createdUtc} /></span> },
    {
      id: "by",
      header: "By",
      render: (r) => (
        <span className="inline-flex items-center gap-1.5">
          <RichTooltip title="Asked from" body={r.origin ?? r.requestedBy} align="start">
            <span className="block max-w-24 truncate font-mono text-[12px]">{r.triggerName ?? r.requestedBy}</span>
          </RichTooltip>
          {r.originUrl && <LinkRef url={r.originUrl} title="The job that asked" />}
        </span>
      ),
    },
    ...(singleRepo ? [] : [{
      id: "repo",
      header: "Repository",
      render: (r: TriggerRequest) => <TruncatedText text={r.repoName} mono maxWidth={100} />,
    }]),
    { id: "sync", header: "Sync", render: (r) => <SyncCell request={r} /> },
    {
      id: "schedules",
      header: "Schedules",
      fill: true,
      floor: 72,
      render: (r) => <SchedulesCell request={r} />,
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (r) => (
        <span className="inline-flex items-center gap-0.5" onClick={(e) => e.stopPropagation()}>
          <IconAction
            label="Copy the summary as Markdown"
            icon={<ClipboardCopy />}
            onClick={() => copySummary.mutate(r)}
            disabled={copySummary.isPending}
            data-testid="request-copy-summary"
          />
          {r.conclusion === null && (
            <IconAction
              label="Cancel the request"
              icon={<CircleStop />}
              onClick={() => setCancelTarget(r)}
              disabled={r.cancelRequestedUtc !== null}
              data-testid="request-cancel"
            />
          )}
        </span>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-3">
      <FilterBar>
        <Select value={triggerId ?? ALL} onValueChange={(value) => onTriggerChange(value === ALL ? null : value)}>
          <SelectTrigger size="sm" className="h-8 w-full sm:w-56" data-testid="requests-trigger-filter">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Every trigger and person</SelectItem>
            {(triggers ?? []).map((t) => <SelectItem key={t.id} value={t.id}>{t.name}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={state} onValueChange={setState}>
          <SelectTrigger size="sm" className="h-8 w-full sm:w-40" data-testid="requests-state-filter">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Any state</SelectItem>
            {STATES.map((s) => <SelectItem key={s} value={s}>{s}</SelectItem>)}
          </SelectContent>
        </Select>
      </FilterBar>

      <PagedTable
        queryKey={["trigger-requests", triggerId ?? ALL, state]}
        fetchPage={(page, pageSize) => triggerRequestApi.list({
          page, pageSize, triggerId: triggerId ?? undefined, state: state === ALL ? undefined : state,
        })}
        columns={columns}
        rowKey={(r) => r.id}
        onRowClick={onOpen}
        pollMs={running ? 3000 : 15000}
        onPageLoaded={(rows) => {
          setRunning(rows.some((r) => r.conclusion === null));
          setSingleRepo(new Set(rows.map((r) => r.repoName)).size <= 1);
        }}
        emptyMessage={triggerId === null && state === ALL
          ? "No request yet. Once a trigger's workflow runs (or someone asks), each request is listed here with its sync, its schedules and their runs."
          : "No request matches these filters."}
        data-testid="requests-table"
      />

      <ConfirmDialog
        open={cancelTarget !== null}
        title="Cancel request"
        message="The request stops waiting for its sync and fires nothing more; the runs it fired are cancelled (a running run is asked to stop). The job following it then fails."
        confirmLabel="Cancel request"
        danger
        busy={cancel.isPending}
        onConfirm={() => { if (cancelTarget !== null) cancel.mutate(cancelTarget); }}
        onClose={() => setCancelTarget(null)}
      />
    </div>
  );
}
