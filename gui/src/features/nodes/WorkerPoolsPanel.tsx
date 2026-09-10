import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Zap } from "lucide-react";
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
import { RelativeTime } from "../../components/RelativeTime";
import { SpawnWorkersDialog } from "./SpawnWorkersDialog";

const poolLabel = (pool: string) => (pool.length === 0 ? "default (untargeted)" : pool);

const headClass = "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground";
const cellClass = "whitespace-nowrap px-3 py-1.5 text-[13px]";

/** Per-pool desired-compute controls: the always-on floor, a bounded manual scale-up, and a one-click spawn, all of
 *  which the control plane applies by writing a catalog row the autoscaler reads (no orchestrator calls). */
export function WorkerPoolsPanel() {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const query = useQuery({
    queryKey: ["nodes", "pools"],
    queryFn: () => nodeApi.listPools(),
    refetchInterval: 5000,
  });

  return (
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="worker-pools-panel">
      <div className="flex flex-col gap-1 p-4 pb-3">
        <h2 className="text-base font-medium">Worker pools</h2>
        <p className="text-[13px] text-muted-foreground">
          The autoscaler runs each pool at the greatest of its queued work, an always-on floor, and any active manual
          override. Keep a worker warm, scale a pool up for a window, or spawn one when the pool is at zero.
        </p>
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
      ) : (query.data ?? []).length === 0 ? (
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
              <TableHead className={cn(headClass, "text-right")}>Target</TableHead>
              <TableHead className={cn(headClass, "text-right")}>Workers</TableHead>
              <TableHead className={headClass}>State</TableHead>
              {canOperate && <TableHead className={headClass}>Controls</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {(query.data ?? []).map((pool) => (
              <WorkerPoolRow key={pool.pool} pool={pool} canOperate={canOperate} />
            ))}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

function WorkerPoolRow({ pool, canOperate }: { pool: WorkerPool; canOperate: boolean }) {
  const queryClient = useQueryClient();
  const [spawnOpen, setSpawnOpen] = useState(false);
  const suffix = pool.pool || "default";
  const startingUp = pool.onlineNodes < pool.replicaTarget;

  const scale = useMutation({
    mutationFn: (request: WorkerPoolScaleRequest) => nodeApi.scalePool(request),
    onSuccess: (result, request) => {
      toast.success(scaleMessage(request, result));
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
        <div className="flex flex-col">
          <span className="font-mono text-[12px] font-medium">{poolLabel(pool.pool)}</span>
          {pool.updatedUtc !== null && (
            <span className="text-xs text-muted-foreground">
              updated <RelativeTime value={pool.updatedUtc} />{pool.updatedBy !== null ? ` by ${pool.updatedBy}` : ""}
            </span>
          )}
        </div>
      </TableCell>
      <TableCell className={cn(cellClass, "text-right font-mono tabular-nums")}>{pool.queuedRuns}</TableCell>
      <TableCell className={cn(cellClass, "text-right font-mono font-medium tabular-nums")}>
        {pool.replicaTarget}
      </TableCell>
      <TableCell className={cn(cellClass, "text-right")}>
        <span className={cn("font-mono font-medium tabular-nums", startingUp && "text-warning")}>
          {pool.onlineNodes} / {pool.replicaTarget}
        </span>
        {startingUp && (
          <span className="flex items-center justify-end gap-1 text-xs text-warning">
            <Loader2 className="size-3.5 animate-spin" />
            starting...
          </span>
        )}
      </TableCell>
      <TableCell className={cellClass}>
        <div className="flex flex-wrap items-center gap-1">
          {pool.minReplicas > 0 && (
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
          {pool.minReplicas === 0 && !pool.manualActive && <Badge variant="outline">autoscale</Badge>}
        </div>
      </TableCell>
      {canOperate && (
        <TableCell className={cellClass}>
          <div className="flex flex-wrap items-center gap-2">
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
                Keep at least one worker running in this pool at all times, so a job never waits for one to start.
              </TooltipContent>
            </Tooltip>
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
                Start workers now for a set time, even with nothing queued.
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
          <SpawnWorkersDialog pool={pool} open={spawnOpen} onClose={() => setSpawnOpen(false)} />
        </TableCell>
      )}
    </TableRow>
  );
}

function scaleMessage(request: WorkerPoolScaleRequest, pool: WorkerPool): string {
  const name = poolLabel(pool.pool);
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
