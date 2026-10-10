// Draws the result of a query that already ran, named by its compute task id. The assistant writes a
// `query-result` fenced block holding the taskId that auto_run_trusted_match (or run_query) returned, and this
// renders the rows that task stored as the same fitting chart or table the Run button shows. It only READS the
// stored task (GET /api/v1/datasources/tasks/{id}); nothing is executed again, so a re-opened conversation shows
// the same answer it showed live.

import { useQuery } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { CorrelationError } from "../../components/CorrelationError";
import { isApiError } from "../../api/client";
import { datasourceApi } from "../../api/endpoints";
import type { RunQueryResult } from "../../api/types";
import { QueryResultView } from "./QueryResultView";

export const TASK_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isRunQueryResult(value: unknown): value is RunQueryResult {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const candidate = value as Partial<RunQueryResult>;
  return Array.isArray(candidate.columns) && Array.isArray(candidate.rows);
}

export function QueryResultEmbed({ code }: { code: string }) {
  const taskId = code.trim();
  const valid = TASK_ID.test(taskId);
  const task = useQuery({
    queryKey: ["compute-task", taskId],
    queryFn: () => datasourceApi.task(taskId),
    enabled: valid,
    staleTime: Infinity,
  });

  if (!valid) {
    return (
      <p className="mb-3 text-xs text-destructive" data-testid="query-result-invalid">
        This answer referenced a query result that could not be identified.
      </p>
    );
  }

  if (task.isLoading) {
    return <Skeleton className="mb-3 h-[160px] w-full rounded-md" />;
  }

  if (task.isError) {
    return (
      <div className="mb-3">
        {isApiError(task.error)
          ? <CorrelationError error={task.error} />
          : <p className="text-xs text-destructive">The query result could not be loaded.</p>}
      </div>
    );
  }

  const data = task.data;
  if (!data || data.status !== "succeeded" || !isRunQueryResult(data.result)) {
    return (
      <p className="mb-3 text-xs text-destructive" data-testid="query-result-unavailable">
        {data?.error ?? `The query did not produce a result (status: ${data?.status ?? "unknown"}).`}
      </p>
    );
  }

  const result = data.result;
  return (
    <div className="mb-3 flex flex-col gap-2 rounded-md border border-border bg-muted/30 p-3" data-testid="query-result-embed">
      {result.truncated && (
        <span className="text-xs text-muted-foreground">
          Showing the first {result.rowCount} row{result.rowCount === 1 ? "" : "s"}.
        </span>
      )}
      <QueryResultView columns={result.columns} rows={result.rows} />
    </div>
  );
}
