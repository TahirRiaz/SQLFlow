import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, History } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { runApi } from "../../api/endpoints";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { FlowKindBadge } from "../../components/FlowKindBadge";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { pollingInterval } from "../../hooks/usePolling";

/** How many runs the home page lists. */
const RECENT = 8;

/**
 * The newest runs, each one line: its outcome, its flow's kind and name, the operation it performed, and when it was
 * written; a line opens the run. The way back into what just happened, on the page a person lands on.
 */
export function RecentRuns() {
  const runs = useQuery({
    queryKey: ["runs", "recent", RECENT],
    queryFn: () => runApi.list({ page: 1, pageSize: RECENT }),
    refetchInterval: pollingInterval(10000),
  });

  return (
    <Card className="gap-0 overflow-hidden rounded-lg py-0" data-testid="home-recent-runs">
      <div className="flex h-11 items-center justify-between border-b px-4">
        <h2 className="text-base font-medium">Recent runs</h2>
        <Button asChild variant="ghost" size="sm" className="-mr-2">
          <Link to="/runs" data-testid="home-recent-runs-all">
            All runs
            <ArrowRight />
          </Link>
        </Button>
      </div>
      {runs.isError
        ? (
          <div className="p-4">
            {isApiError(runs.error) ? <CorrelationError error={runs.error} /> : <p className="text-[13px] text-destructive">{String(runs.error)}</p>}
          </div>
        )
        : runs.data === undefined
          ? (
            <div className="flex flex-col">
              {Array.from({ length: 5 }, (_, index) => (
                <div key={`recent-skeleton-${index}`} className="flex h-9 items-center gap-3 border-b px-4 last:border-b-0">
                  <Skeleton className="size-5 rounded-full" />
                  <Skeleton className="h-4 flex-1" />
                </div>
              ))}
            </div>
          )
          : runs.data.items.length === 0
            ? (
              <div className="p-4">
                <EmptyState icon={<History />} title="No runs yet" description="Trigger a flow and its runs appear here as they are queued." />
              </div>
            )
            : (
              <ul className="flex flex-col">
                {runs.data.items.map((run) => (
                  <li key={run.runId} className="border-b last:border-b-0">
                    <Link
                      to={`/runs/${run.runId}`}
                      className="flex h-9 items-center gap-3 px-4 transition-colors hover:bg-accent/50"
                      data-testid="home-recent-run"
                    >
                      <RunStatusBadge status={run.status} testId="home-recent-run-status" />
                      <FlowKindBadge kind={run.flowKind} testId="home-recent-run-kind" />
                      <span className="min-w-0 flex-1 truncate font-mono text-[12px]" title={run.flowName}>{run.flowName}</span>
                      {run.operation !== null && <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{run.operation}</span>}
                      <span className="shrink-0 text-xs text-muted-foreground tabular-nums">
                        <RelativeTime value={run.writtenUtc} />
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
    </Card>
  );
}
