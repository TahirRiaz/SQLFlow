import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, TriangleAlert, Zap } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { WorkerPool, WorkerPoolScaleRequest } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { ExplainMark } from "../../components/ExplainTip";
import { RelativeTime } from "../../components/RelativeTime";
import { useWorkerPools } from "./nodeQueries";
import { scalingModeExplanation, scalingModeOf } from "./scalingMode";
import { SpawnWorkersDialog } from "./SpawnWorkersDialog";

const poolLabel = (pool: string) => (pool.length === 0 ? "default (untargeted)" : pool);

const headClass = "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground";
const cellClass = "whitespace-nowrap px-3 py-1.5 text-[13px]";

/** Per-pool desired-compute controls: the always-on floor, a bounded manual scale-up, and a one-click spawn, all of
 *  which the control plane applies by writing a catalog row. Without managed workers its own scale-target endpoint
 *  folds that row into the replica target the autoscaler reads (no orchestrator calls), and the target shown is exactly
 *  what the scaler is answered with. With managed workers on, the control plane starts and stops each managed pool's
 *  workers itself, and the row shows the pool as the managed fleet's last tick saw it. With neither, and the control
 *  plane running runs on its own node, the panel says so: nothing it shows starts a worker unless an autoscaler reads
 *  the target, which the control plane cannot see. */
export function WorkerPoolsPanel() {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const query = useWorkerPools();
  const pools = query.data ?? [];
  const mode = scalingModeOf(pools);
  const launcher = mode.kind === "managed" ? mode.launcher : null;
  const builtInNode = mode.kind === "built-in" ? mode.node : null;

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="worker-pools-panel">
      <div className="flex items-center gap-2 px-4 pt-3 pb-2">
        <h2 className="text-sm font-medium">Worker pools</h2>
        <span className="text-xs text-muted-foreground" data-testid={`worker-pools-mode-${mode.kind}`}>
          {mode.kind === "managed"
            ? `started by the control plane through the ${mode.launcher} launcher`
            : mode.kind === "built-in"
              ? "nothing here starts workers"
              : "held at their targets by an autoscaler"}
        </span>
        <ExplainMark title="How workers start" text={scalingModeExplanation(mode)} testId="worker-pools-explain" />
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
      ) : pools.length === 0 ? (
        <EmptyState
          title="No worker pools yet."
          description="Pools appear here once a worker registers or a flow targets one."
        />
      ) : (
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead className={headClass}>Pool</TableHead>
              <TableHead className={cn(headClass, "text-right")}>Queued</TableHead>
              <TableHead className={cn(headClass, "text-right")}>Busy</TableHead>
              <TableHead className={cn(headClass, "text-right")}>Target</TableHead>
              <TableHead className={cn(headClass, "text-right")}>Workers</TableHead>
              <TableHead className={headClass}>State</TableHead>
              {canOperate && <TableHead className={headClass}>Controls</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {pools.map((pool) => (
              <WorkerPoolRow
                key={pool.pool}
                pool={pool}
                canOperate={canOperate}
                managedMode={launcher !== null}
                builtInMode={builtInNode !== null}
              />
            ))}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

function WorkerPoolRow({
  pool,
  canOperate,
  managedMode,
  builtInMode,
}: {
  pool: WorkerPool;
  canOperate: boolean;
  managedMode: boolean;
  /** Managed workers are off and this control plane runs runs on its own node: nothing it can see starts a worker. */
  builtInMode: boolean;
}) {
  const queryClient = useQueryClient();
  const [spawnOpen, setSpawnOpen] = useState(false);
  const suffix = pool.pool || "default";
  const managed = pool.managedBy === null ? null : pool.managed;
  const unmanagedInManagedMode = managedMode && pool.managedBy === null;
  const target = managed?.target ?? pool.replicaTarget;
  const working = managed === null ? pool.onlineNodes : managed.ready + managed.due;
  const belowTarget = builtInMode && pool.onlineNodes < pool.replicaTarget;
  const startingUp = unmanagedInManagedMode || builtInMode
    ? false
    : managed === null ? pool.onlineNodes < pool.replicaTarget : managed.starting > 0;

  const scale = useMutation({
    mutationFn: (request: WorkerPoolScaleRequest) => nodeApi.scalePool(request),
    onSuccess: (result, request) => {
      toast.success(scaleMessage(request, result, builtInMode));
      void queryClient.invalidateQueries({ queryKey: ["nodes", "pools"] });
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  return (
    <TableRow data-testid={`worker-pool-row-${suffix}`}>
      <TableCell className={cellClass}>
        {pool.updatedUtc !== null ? (
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="font-mono text-[12px] font-medium">{poolLabel(pool.pool)}</span>
            </TooltipTrigger>
            <TooltipContent>
              Floor last changed <RelativeTime value={pool.updatedUtc} />
              {pool.updatedBy !== null ? ` by ${pool.updatedBy}` : ""}.
            </TooltipContent>
          </Tooltip>
        ) : (
          <span className="font-mono text-[12px] font-medium">{poolLabel(pool.pool)}</span>
        )}
      </TableCell>
      <TableCell className={cn(cellClass, "text-right")}>
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="flex flex-col items-end">
              <span className="font-mono tabular-nums">{pool.queuedRuns}</span>
              {pool.eligibleQueuedRuns !== pool.queuedRuns && (
                <span className="text-xs text-muted-foreground" data-testid={`worker-pool-eligible-${suffix}`}>
                  {pool.eligibleQueuedRuns} eligible
                </span>
              )}
            </span>
          </TooltipTrigger>
          <TooltipContent className="max-w-xs">
            {pool.eligibleQueuedRuns} of the {pool.queuedRuns} queued runs are eligible: no gate holds them back. A run
            behind a busy pipeline, a lower wave or a full group cap asks for no worker. One worker here executes{" "}
            {pool.runSlotsPerNode} at once
            {pool.eligibleWholeNodeRuns > 0
              ? `, and ${pool.eligibleWholeNodeRuns} of the eligible runs take a worker each to themselves.`
              : "."}
          </TooltipContent>
        </Tooltip>
      </TableCell>
      <TableCell className={cn(cellClass, "text-right font-mono tabular-nums")}>
        <Tooltip>
          <TooltipTrigger asChild>
            <span>{managed?.busy ?? pool.busyNodes}</span>
          </TooltipTrigger>
          <TooltipContent className="max-w-xs">
            Online workers holding at least one run; each keeps its replica until it is idle.
          </TooltipContent>
        </Tooltip>
      </TableCell>
      <TableCell className={cn(cellClass, "text-right font-mono font-medium tabular-nums")}>
        {unmanagedInManagedMode ? <span className="text-muted-foreground">-</span> : target}
      </TableCell>
      <TableCell className={cn(cellClass, "text-right")}>
        <span className={cn("font-mono font-medium tabular-nums", startingUp && "text-warning")}>
          {unmanagedInManagedMode ? working : `${working} / ${target}`}
        </span>
        {startingUp && (
          <span className="flex items-center justify-end gap-1 text-xs text-warning">
            <Loader2 className="size-3.5 animate-spin" />
            {managed === null ? "starting..." : `${managed.starting} starting`}
          </span>
        )}
        {belowTarget && (
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="block text-right text-xs text-muted-foreground" data-testid={`worker-pool-below-target-${suffix}`}>
                below target
              </span>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs">
              {pool.onlineNodes} of {pool.replicaTarget} workers are online. Managed workers are off, so this control plane
              starts none; only an autoscaler reading the target would.
            </TooltipContent>
          </Tooltip>
        )}
        {managed !== null && managed.due > 0 && (
          <span className="block text-right text-xs text-muted-foreground">{managed.due} replacing</span>
        )}
        {managed !== null && managed.draining > 0 && (
          <span className="block text-right text-xs text-muted-foreground">{managed.draining} leaving</span>
        )}
        {managed !== null && managed.silent > 0 && (
          <span className="block text-right text-xs text-warning">{managed.silent} silent</span>
        )}
      </TableCell>
      <TableCell className={cn(cellClass, "min-w-[6rem] max-w-[11rem] whitespace-normal")}>
        <div className="flex flex-wrap items-center gap-1">
          {pool.managedBy !== null && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" data-testid={`worker-pool-managed-${suffix}`}>
                  managed
                </Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                The control plane starts this pool's workers through the {pool.managedBy} launcher and always keeps at
                least {managed !== null ? managed.minWorkers : "its minimum"} running
                {managed !== null ? `, up to ${managed.maxWorkers} for queued runs` : ""}. The always-on floor and Spawn
                raise the minimum, up to the maximum.
              </TooltipContent>
            </Tooltip>
          )}
          {managed !== null && managed.lastError !== null && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span
                  className="inline-flex items-center gap-1 text-xs text-muted-foreground"
                  data-testid={`worker-pool-start-failing-${suffix}`}
                >
                  <TriangleAlert className="size-3.5 text-warning" aria-hidden />
                  failing
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-sm">
                {managed.consecutiveFailures} failed start(s) in a row: {managed.lastError}
                {managed.blockedUntilUtc !== null && (
                  <>
                    {" "}Next try <RelativeTime value={managed.blockedUntilUtc} />.
                  </>
                )}
              </TooltipContent>
            </Tooltip>
          )}
          {unmanagedInManagedMode && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" className="text-muted-foreground">not managed</Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                No worker is started for this pool. List it under ControlPlane:ManagedWorkers:Pools to manage it.
              </TooltipContent>
            </Tooltip>
          )}
          {pool.builtInNode !== null && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" data-testid={`worker-pool-built-in-${suffix}`}>
                  built-in node
                </Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                This control plane runs the pool&apos;s runs on its own node, {pool.builtInNode.name},{" "}
                {pool.builtInNode.runSlots} at a time. It counts among the online workers, and nothing starts or stops it
                but the control plane.
              </TooltipContent>
            </Tooltip>
          )}
          {pool.managedBy === null && !unmanagedInManagedMode && pool.minReplicas > 0 && (
            <Badge variant="outline" className="border-success/40 text-success">
              always-on {pool.minReplicas}
            </Badge>
          )}
          {pool.manualActive && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge variant="outline" className="border-info/40 text-info">
                  manual {pool.manualReplicas}
                </Badge>
              </TooltipTrigger>
              <TooltipContent>
                reverts <RelativeTime value={pool.manualUntilUtc} />
              </TooltipContent>
            </Tooltip>
          )}
          {!managedMode && !builtInMode && pool.minReplicas === 0 && !pool.manualActive && (
            <Badge variant="outline">autoscale</Badge>
          )}
        </div>
      </TableCell>
      {canOperate && (
        <TableCell className={cellClass}>
          {!unmanagedInManagedMode && (
            <div className="flex flex-wrap items-center gap-2">
              {pool.managedBy === null && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Label className="h-8 gap-1.5 whitespace-nowrap text-xs font-normal">
                      <Switch
                        size="sm"
                        checked={pool.minReplicas > 0}
                        disabled={scale.isPending}
                        onCheckedChange={(checked) =>
                          scale.mutate({ pool: pool.pool, minReplicas: checked ? 1 : 0 })}
                        data-testid={`worker-pool-alwayson-${suffix}`}
                      />
                      Always on
                    </Label>
                  </TooltipTrigger>
                  <TooltipContent className="max-w-xs">
                    {builtInMode
                      ? "Ask for at least one worker in this pool at all times. Managed workers are off, so this control plane starts none; only an autoscaler reading the target would."
                      : "Keep at least one worker running in this pool at all times, so a job never waits for one to start."}
                  </TooltipContent>
                </Tooltip>
              )}
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={scale.isPending}
                    onClick={() => setSpawnOpen(true)}
                    data-testid={`worker-pool-spawn-${suffix}`}
                  >
                    <Zap />
                    Spawn
                  </Button>
                </TooltipTrigger>
                <TooltipContent className="max-w-xs">
                  {builtInMode
                    ? "Raise the pool's target for a set time, even with nothing queued. Managed workers are off, so this control plane starts no worker; only an autoscaler reading the target would."
                    : "Start workers now for a set time, even with nothing queued."}
                </TooltipContent>
              </Tooltip>
              {pool.manualActive && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={scale.isPending}
                  onClick={() => scale.mutate({ pool: pool.pool, manualReplicas: 0 })}
                  data-testid={`worker-pool-stop-${suffix}`}
                >
                  Stop ({pool.manualReplicas})
                </Button>
              )}
            </div>
          )}
          <SpawnWorkersDialog
            pool={pool}
            open={spawnOpen}
            onClose={() => setSpawnOpen(false)}
            builtInMode={builtInMode}
          />
        </TableCell>
      )}
    </TableRow>
  );
}

function scaleMessage(request: WorkerPoolScaleRequest, pool: WorkerPool, builtInMode: boolean): string {
  const name = poolLabel(pool.pool);
  if (builtInMode) {
    // Nothing the control plane can see acts on the target, so the toast says what was recorded, not what will start.
    if (request.minReplicas !== undefined && request.manualReplicas === undefined) {
      return request.minReplicas > 0
        ? `Always-on floor of ${request.minReplicas} recorded for ${name}; this control plane starts no worker for it.`
        : `Always-on floor cleared for ${name}.`;
    }
    if (request.manualReplicas === 0) {
      return `Manual override cleared for ${name}.`;
    }
    return `${name}'s target is ${pool.replicaTarget} until the override reverts; this control plane starts no worker for it.`;
  }
  if (request.minReplicas !== undefined && request.manualReplicas === undefined) {
    return request.minReplicas > 0
      ? `Keeping at least ${request.minReplicas} worker warm in ${name}.`
      : `Always-on disabled for ${name}; it will scale to zero when idle.`;
  }
  if (request.manualReplicas === 0) {
    return `Manual override cleared for ${name}.`;
  }
  return `${name} will run ${pool.replicaTarget} worker(s); the override reverts automatically.`;
}
