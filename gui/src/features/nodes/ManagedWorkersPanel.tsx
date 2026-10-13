import { useQuery } from "@tanstack/react-query";
import { Activity, CircleX, PauseCircle, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { nodeApi } from "../../api/endpoints";
import type {
  ManagedFleetStatus,
  ManagedWorkerAction,
  ManagedWorkerActionKind,
  ManagedWorkerPhase,
  ManagedWorkerView,
} from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { StatePill } from "../../components/StatusBadge";

const headClass = "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground";
const cellClass = "whitespace-nowrap px-3 py-1.5 text-[13px]";

const poolLabel = (pool: string | null) => (pool === null ? "not managed" : pool.length === 0 ? "default" : pool);

/** What each action is called on the page; a failed one carries a glyph, the rest are plain. */
const actionLabels: Record<ManagedWorkerActionKind, string> = {
  start: "started",
  "start-failed": "start failed",
  drain: "asked to leave",
  stop: "stopped",
  "stop-failed": "stop failed",
  ended: "ended",
};

/** What each worker phase means, for the tooltip on its chip. */
const phaseHints: Record<ManagedWorkerPhase, string> = {
  Starting: "Started; it has not polled the control plane yet.",
  Ready: "Polling and taking work.",
  Due: "Being replaced (too old, or started by another build of the control plane); it takes work until its replacement is ready.",
  Draining: "Takes no new work and leaves once it holds nothing.",
  Silent: "Polled before but not within the last minute: it takes no work, still counts for two minutes after its last poll, then a replacement starts; it is stopped if it stays silent.",
  Lost: "Never polled in time, went silent, or ignored the request to leave; it is stopped.",
  Ended: "The platform reports it ended.",
  Leftover: "Polls under a managed name the launcher does not have; it is asked to leave.",
};

/** Managed workers: the control plane starts and stops its own workers through a launcher. Shows whether this replica
 *  is the one doing it, the launcher's health, every managed worker, and what the fleet did recently and why. Renders
 *  nothing when managed workers are off. */
export function ManagedWorkersPanel() {
  const query = useQuery({
    queryKey: ["nodes", "managed"],
    queryFn: () => nodeApi.managed(),
    refetchInterval: 5000,
  });

  const status = query.data?.status ?? null;
  if (query.data === undefined || !query.data.enabled || status === null) {
    return null;
  }

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="managed-workers-panel">
      <div className="flex flex-col gap-1 p-4 pb-3">
        <h2 className="text-base font-medium">Managed workers</h2>
        <Summary status={status} />
      </div>
      <div className="flex flex-col gap-4 px-4 pb-4">
        <WorkersTable workers={status.workers} />
        <ActionsTable actions={status.actions} />
      </div>
    </Card>
  );
}

function Summary({ status }: { status: ManagedFleetStatus }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-muted-foreground" data-testid="managed-workers-summary">
      {status.active
        ? <StatePill tone="success" label="managing" icon={Activity} testId="managed-workers-state" />
        : <StatePill tone="muted" label="passive" icon={PauseCircle} testId="managed-workers-state" />}
      <span>
        launcher <span className="font-mono text-foreground">{status.launcher}</span>
      </span>
      {status.lastTickUtc !== null && (
        <span>
          checked <RelativeTime value={status.lastTickUtc} />
        </span>
      )}
      {!status.active && <span>another control plane replica owns dispatch and manages the workers</span>}
      {status.active && !status.listCurrent && (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="inline-flex items-center gap-1" data-testid="managed-workers-list-error">
              <TriangleAlert className="size-3.5 text-warning" aria-hidden />
              launcher not answering
            </span>
          </TooltipTrigger>
          <TooltipContent className="max-w-sm">
            {status.listError ?? "The launcher did not answer."} Nothing is stopped and only pool minimums are restored
            until it answers{status.lastListUtc !== null ? "; it last answered " : "."}
            {status.lastListUtc !== null && <RelativeTime value={status.lastListUtc} />}
          </TooltipContent>
        </Tooltip>
      )}
    </div>
  );
}

function WorkersTable({ workers }: { workers: ManagedWorkerView[] }) {
  return (
    <Section title="Workers" count={workers.length}>
      {workers.length === 0 ? (
        <p className="px-3 py-2 text-[13px] text-muted-foreground">No managed worker yet.</p>
      ) : (
        <div className="max-h-64 overflow-y-auto">
          <Table className="table-fixed" data-testid="managed-workers-table">
            <TableHeader className="sticky top-0 bg-card">
              <TableRow className="hover:bg-transparent">
                <TableHead className={cn(headClass, "w-[14rem]")}>Worker</TableHead>
                <TableHead className={cn(headClass, "w-[5rem]")}>Pool</TableHead>
                <TableHead className={cn(headClass, "w-[6.5rem]")}>State</TableHead>
                <TableHead className={cn(headClass, "w-[3.5rem] text-right")}>Runs</TableHead>
                <TableHead className={headClass}>Started</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {workers.map((worker) => (
                <TableRow key={worker.name} data-testid={`managed-worker-${worker.name}`}>
                  <TableCell className={cn(cellClass, "truncate font-mono text-[12px]")} title={worker.name}>{worker.name}</TableCell>
                  <TableCell className={cn(cellClass, "truncate font-mono text-[12px]")}>{poolLabel(worker.pool)}</TableCell>
                  <TableCell className={cellClass}>
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <Badge variant="outline" className={cn((worker.phase === "Lost" || worker.phase === "Silent") && "gap-1")}>
                          {(worker.phase === "Lost" || worker.phase === "Silent") && <TriangleAlert className="size-3 text-warning" aria-hidden />}
                          {worker.phase.toLowerCase()}
                        </Badge>
                      </TooltipTrigger>
                      <TooltipContent className="max-w-xs">
                        {phaseHints[worker.phase]}
                        {worker.idleSinceUtc !== null && (
                          <>
                            {" "}Idle since <RelativeTime value={worker.idleSinceUtc} />.
                          </>
                        )}
                      </TooltipContent>
                    </Tooltip>
                  </TableCell>
                  <TableCell className={cn(cellClass, "text-right font-mono tabular-nums", worker.busyRuns === 0 && "text-muted-foreground")}>
                    {worker.busyRuns}
                  </TableCell>
                  <TableCell className={cn(cellClass, "truncate")}>
                    {worker.launchedUtc !== null ? <RelativeTime value={worker.launchedUtc} /> : <span className="text-muted-foreground">-</span>}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </Section>
  );
}

function ActionsTable({ actions }: { actions: ManagedWorkerAction[] }) {
  return (
    <Section title="Recent actions" count={actions.length}>
      {actions.length === 0 ? (
        <p className="px-3 py-2 text-[13px] text-muted-foreground">Nothing started or stopped yet.</p>
      ) : (
        <ul className="max-h-72 divide-y overflow-y-auto" data-testid="managed-actions-list">
          {actions.map((action, index) => (
            <li key={`${action.timestampUtc}-${action.kind}-${action.worker ?? ""}-${index}`} className="flex flex-col gap-0.5 px-3 py-1.5">
              <div className="flex min-w-0 items-center gap-3 text-[13px]">
                <span className="w-[9rem] shrink-0 text-muted-foreground">
                  <RelativeTime value={action.timestampUtc} />
                </span>
                <span className="w-[7.5rem] shrink-0">
                  <ActionLabel kind={action.kind} />
                </span>
                <span className="min-w-0 truncate font-mono text-[12px]" title={action.worker ?? undefined}>
                  {action.worker ?? poolLabel(action.pool)}
                </span>
              </div>
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="truncate text-xs text-muted-foreground">{action.message}</span>
                </TooltipTrigger>
                <TooltipContent className="max-w-md">{action.message}</TooltipContent>
              </Tooltip>
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}

function ActionLabel({ kind }: { kind: ManagedWorkerActionKind }) {
  const failed = kind === "start-failed" || kind === "stop-failed";
  return (
    <span className="inline-flex items-center gap-1">
      {failed && <CircleX className="size-3.5 text-destructive" aria-hidden />}
      {actionLabels[kind] ?? kind}
    </span>
  );
}

function Section({ title, count, children }: { title: string; count: number; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-sm font-medium">
        {title} <span className="font-mono text-xs tabular-nums text-muted-foreground">{count}</span>
      </h3>
      <div className="overflow-hidden rounded-md border">{children}</div>
    </div>
  );
}
