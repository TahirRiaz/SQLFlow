import { Link } from "react-router-dom";
import { Activity, PauseCircle } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import type { DispatchBlockReason, DispatchSnapshot, LeasedRunView, QueuedRunView } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { ExplainMark } from "../../components/ExplainTip";
import { RelativeTime } from "../../components/RelativeTime";
import { StatePill } from "../../components/StatusBadge";
import { useDispatchSnapshot } from "./nodeQueries";

const poolLabel = (pool: string) => (pool.length === 0 ? "default" : pool);

const headClass = "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground";
const cellClass = "whitespace-nowrap px-3 py-1.5 text-[13px]";
const numClass = cn(cellClass, "text-right font-mono tabular-nums");

/** The longest list the panel renders in full; beyond it the newest rows are shown and the rest counted, because a
 *  wave of hundreds of queued members is read as a number, not scrolled. */
const MaxRows = 40;

/** What the dispatcher says about a queued run, in the operator's words. */
const blockLabels: Record<DispatchBlockReason, { label: string; hint: string }> = {
  "": { label: "eligible", hint: "Goes to the next node with a free slot that serves its pool." },
  "pipeline-busy": {
    label: "pipeline busy",
    hint: "Another run of the same pipeline is executing, or is ahead of this one in the queue; this one waits its turn.",
  },
  "wave-gated": { label: "wave gated", hint: "A lower wave of the run's group has not finished." },
  "group-cap": {
    label: "group cap",
    hint: "The run's group is executing as many members as its cap allows, counting the members ahead of this one in the queue.",
  },
  "no-eligible-node": { label: "no node", hint: "No online node serves the run's pool." },
};

/** The dispatcher as it sees itself: ownership and its housekeeping passes, the per-pool backlog against the
 *  capacity online to take it, every queued run with the gate holding it back, and every lease with the node that
 *  holds it. Read-only, refreshed every few seconds; this is the diagnostic for "why is my run not starting". */
export function DispatchPanel() {
  const query = useDispatchSnapshot();

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="dispatch-panel">
      <div className="flex items-center gap-2 px-4 pt-3 pb-2">
        <h2 className="text-sm font-medium">Dispatcher</h2>
        {query.data !== undefined && <Ownership snapshot={query.data} />}
        <ExplainMark
          title="The dispatcher"
          text="The control plane holds the run queue in memory and hands work to the nodes that poll for it. Every queued run shows the gate holding it back (a run of the same flow executing, a lower wave of its group, its group's cap, or no online node for its pool), and every running one the node that holds its lease."
          testId="dispatch-explain"
        />
      </div>

      {query.isLoading ? (
        <div className="px-4 pb-4">
          <Skeleton className="h-40 w-full rounded-md" />
        </div>
      ) : query.isError ? (
        <div className="px-4 pb-4">
          {isApiError(query.error)
            ? <CorrelationError error={query.error} />
            : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
        </div>
      ) : query.data === undefined ? null : (
        <DispatchBody snapshot={query.data} />
      )}
    </Card>
  );
}

function DispatchBody({ snapshot }: { snapshot: DispatchSnapshot }) {
  // The per-pool counts repeat the page's summary while there is one pool and no compute task, so they show only when
  // they say something the summary does not.
  const poolsSaySomething = snapshot.pools.length > 1
    || snapshot.pools.some((pool) => pool.queuedTasks > 0 || pool.leasedTasks > 0);
  const idle = snapshot.queuedRuns.length === 0 && snapshot.leasedRuns.length === 0;
  return (
    <div className="flex flex-col gap-4 px-4 pb-4">
      {!snapshot.active ? (
        <EmptyState
          title="This replica does not own dispatch."
          description="Another control-plane replica holds the dispatch lease and serves the nodes; this one journals enqueues and answers reads. The queue is visible on the owner."
        />
      ) : (
        <>
          {poolsSaySomething && <PoolsTable snapshot={snapshot} />}
          {idle && (
            <p className="text-[13px] text-muted-foreground" data-testid="dispatch-idle">
              Nothing is queued or running.
            </p>
          )}
          <LeasedRunsTable runs={snapshot.leasedRuns} />
          <QueuedRunsTable runs={snapshot.queuedRuns} />
        </>
      )}
    </div>
  );
}

/** Whether this replica owns dispatch, as one chip; who owns it, since when and its housekeeping are behind it. */
function Ownership({ snapshot }: { snapshot: DispatchSnapshot }) {
  return (
    <HoverCard openDelay={250} closeDelay={150}>
      <HoverCardTrigger asChild>
        <span className="inline-flex cursor-default" data-testid="dispatch-ownership">
          {snapshot.active
            ? <StatePill tone="success" label="active" icon={Activity} testId="dispatch-state" />
            : <StatePill tone="muted" label="passive" icon={PauseCircle} testId="dispatch-state" />}
        </span>
      </HoverCardTrigger>
      <HoverCardContent align="start" className="w-auto max-w-md p-3" data-testid="dispatch-ownership-details">
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-[12.5px]">
          <dt className="text-muted-foreground">Owner</dt>
          <dd className="break-all font-mono">{snapshot.owner ?? "-"}</dd>
          <dt className="text-muted-foreground">Since</dt>
          <dd>{snapshot.activatedUtc !== null ? <RelativeTime value={snapshot.activatedUtc} /> : "-"}</dd>
          <dt className="text-muted-foreground">Reconciled</dt>
          <dd>{snapshot.lastReconcileUtc !== null ? <RelativeTime value={snapshot.lastReconcileUtc} /> : "-"}</dd>
          <dt className="text-muted-foreground">Housekeeping</dt>
          <dd>{snapshot.lastTickUtc !== null ? <RelativeTime value={snapshot.lastTickUtc} /> : "-"}</dd>
          <dt className="text-muted-foreground">Parked polls</dt>
          <dd className="font-mono tabular-nums">{snapshot.waiters}</dd>
        </dl>
      </HoverCardContent>
    </HoverCard>
  );
}

/** The backlog and capacity per pool, shown only when there is more than one pool or a compute task is in flight. */
function PoolsTable({ snapshot }: { snapshot: DispatchSnapshot }) {
  return (
    <Section title="Pools" count={snapshot.pools.length}>
      <Table data-testid="dispatch-pools-table">
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead className={headClass}>Pool</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Queued</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Running</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Tasks queued</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Tasks running</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Nodes online</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Free slots</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {snapshot.pools.map((pool) => (
            <TableRow key={pool.pool} data-testid={`dispatch-pool-row-${pool.pool || "default"}`}>
              <TableCell className={cn(cellClass, "font-mono text-[12px] font-medium")}>{poolLabel(pool.pool)}</TableCell>
              <TableCell className={numClass}>{pool.queuedRuns}</TableCell>
              <TableCell className={numClass}>{pool.leasedRuns}</TableCell>
              <TableCell className={numClass}>{pool.queuedTasks}</TableCell>
              <TableCell className={numClass}>{pool.leasedTasks}</TableCell>
              <TableCell className={cn(numClass, pool.queuedRuns > 0 && pool.onlineNodes === 0 && "text-warning")}>
                {pool.onlineNodes}
              </TableCell>
              <TableCell className={numClass}>{pool.freeRunSlots}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

function QueuedRunsTable({ runs }: { runs: QueuedRunView[] }) {
  if (runs.length === 0) {
    return null;
  }

  const shown = runs.slice(0, MaxRows);
  // How many wait behind each gate, in the order the gates are listed, so the reason the queue is not moving reads at a
  // glance and the rows below are only needed for a particular run.
  const byGate = (Object.keys(blockLabels) as DispatchBlockReason[])
    .map((gate) => ({ gate, count: runs.filter((run) => run.blocked === gate).length }))
    .filter((entry) => entry.count > 0);
  return (
    <Section
      title="Queued runs"
      count={runs.length}
      aside={
        <span className="text-xs font-normal text-muted-foreground" data-testid="dispatch-queued-by-gate">
          {byGate.map((entry) => `${entry.count} ${blockLabels[entry.gate].label}`).join(" · ")}
        </span>
      }
    >
      <div className="max-h-80 overflow-y-auto">
      <Table data-testid="dispatch-queued-table">
        <TableHeader className="sticky top-0 bg-card">
          <TableRow className="hover:bg-transparent">
            <TableHead className={headClass}>Run</TableHead>
            <TableHead className={headClass}>Pool</TableHead>
            <TableHead className={headClass}>Gate</TableHead>
            <TableHead className={headClass}>Group</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Attempt</TableHead>
            <TableHead className={headClass}>Queued</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {shown.map((run) => (
            <TableRow key={run.runId} data-testid="dispatch-queued-row">
              <TableCell className={cellClass}>
                <RunLink runId={run.runId} />
                {run.cancelRequested && (
                  <Badge variant="outline" className="ml-2 border-warning/40 text-warning">cancel requested</Badge>
                )}
              </TableCell>
              <TableCell className={cn(cellClass, "font-mono text-[12px]")}>{poolLabel(run.pool)}</TableCell>
              <TableCell className={cellClass}>
                <BlockBadge blocked={run.blocked} />
              </TableCell>
              <TableCell className={cn(cellClass, "font-mono text-[12px] text-muted-foreground")}>
                {run.groupId === null
                  ? "-"
                  : `${run.groupId.slice(0, 8)} wave ${run.groupWave}${run.groupMaxConcurrency === null ? "" : ` cap ${run.groupMaxConcurrency}`}`}
              </TableCell>
              <TableCell className={numClass}>{run.attempt}</TableCell>
              <TableCell className={cellClass}><RelativeTime value={run.enqueuedUtc} /></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
      <Overflow total={runs.length} shown={shown.length} />
    </Section>
  );
}

function LeasedRunsTable({ runs }: { runs: LeasedRunView[] }) {
  if (runs.length === 0) {
    return null;
  }

  const shown = runs.slice(0, MaxRows);
  return (
    <Section title="Running runs" count={runs.length}>
      <Table data-testid="dispatch-leased-table">
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead className={headClass}>Run</TableHead>
            <TableHead className={headClass}>Pool</TableHead>
            <TableHead className={headClass}>Node</TableHead>
            <TableHead className={cn(headClass, "text-right")}>Attempt</TableHead>
            <TableHead className={headClass}>Lease</TableHead>
            <TableHead className={headClass}>Since</TableHead>
            <TableHead className={headClass}>Expires</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {shown.map((run) => (
            <TableRow key={run.runId} data-testid="dispatch-leased-row">
              <TableCell className={cellClass}>
                <RunLink runId={run.runId} />
                {run.cancelRequested && (
                  <Badge variant="outline" className="ml-2 border-warning/40 text-warning">cancel requested</Badge>
                )}
              </TableCell>
              <TableCell className={cn(cellClass, "font-mono text-[12px]")}>{poolLabel(run.pool)}</TableCell>
              <TableCell className={cn(cellClass, "font-mono text-[12px]")}>{run.node ?? "-"}</TableCell>
              <TableCell className={numClass}>{run.attempt}</TableCell>
              <TableCell className={cellClass}>
                <Badge variant="outline" className={cn(run.state === "expiring" && "border-warning/40 text-warning")}>
                  {run.state}
                </Badge>
              </TableCell>
              <TableCell className={cellClass}>{run.leasedUtc === null ? "-" : <RelativeTime value={run.leasedUtc} />}</TableCell>
              <TableCell className={cellClass}>{run.leaseExpiresUtc === null ? "-" : <RelativeTime value={run.leaseExpiresUtc} />}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <Overflow total={runs.length} shown={shown.length} />
    </Section>
  );
}

function Section({ title, count, aside, children }: {
  title: string;
  count: number;
  aside?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-2">
      <h3 className="flex items-baseline gap-3 text-sm font-medium">
        <span>
          {title} <span className="font-mono text-xs tabular-nums text-muted-foreground">{count}</span>
        </span>
        {aside}
      </h3>
      <div className="overflow-hidden rounded-md border">{children}</div>
    </div>
  );
}

function Overflow({ total, shown }: { total: number; shown: number }) {
  if (total <= shown) {
    return null;
  }

  return (
    <p className="text-xs text-muted-foreground">
      and <span className="font-mono tabular-nums">{total - shown}</span> more, oldest first above.
    </p>
  );
}

/** The run's short id, linking to its detail page; the full id travels in the title for a hover and a copy. */
function RunLink({ runId }: { runId: string }) {
  return (
    <Link to={`/runs/${runId}`} className="font-mono text-[12px] text-primary hover:underline" title={runId}>
      {runId.slice(0, 8)}
    </Link>
  );
}

function BlockBadge({ blocked }: { blocked: DispatchBlockReason }) {
  const { label, hint } = blockLabels[blocked] ?? { label: blocked, hint: "" };
  const eligible = blocked === "";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Badge variant="outline" className={cn(eligible ? "border-success/40 text-success" : "text-muted-foreground")}>
          {label}
        </Badge>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">{hint}</TooltipContent>
    </Tooltip>
  );
}
