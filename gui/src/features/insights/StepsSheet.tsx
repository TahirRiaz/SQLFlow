import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { insightsApi } from "../../api/endpoints";
import type { StepInsight } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { formatDurationSeconds } from "../../lib/time";

function formatMs(ms: number): string {
  return ms < 1000 ? `${ms.toFixed(0)} ms` : formatDurationSeconds(ms / 1000);
}

const stepColumns: Column<StepInsight>[] = [
  { id: "step", header: "Step", render: (s) => <span className="font-mono text-xs">{s.step}</span> },
  {
    id: "occurrences",
    header: "Times",
    align: "right",
    render: (s) => <span className="font-mono tabular-nums">{s.occurrences}</span>,
  },
  {
    id: "avg",
    header: "Avg",
    align: "right",
    render: (s) => <span className="font-mono tabular-nums">{formatMs(s.avgElapsedMs)}</span>,
  },
  {
    id: "max",
    header: "Max",
    align: "right",
    render: (s) => <span className="font-mono tabular-nums">{formatMs(s.maxElapsedMs)}</span>,
  },
  {
    id: "total",
    header: "Total",
    align: "right",
    render: (s) => <span className="font-mono tabular-nums">{formatMs(s.totalElapsedMs)}</span>,
  },
  {
    id: "rows",
    header: "Rows",
    align: "right",
    render: (s) => <span className="font-mono tabular-nums">{s.rowsProcessed.toLocaleString()}</span>,
  },
];

interface StepsSheetProps {
  pipelineId: string | null;
  flowName: string | null;
  windowDays: number;
  onClose: () => void;
}

/**
 * The per-flow drill-down: which engine steps eat the flow's time (averaged across the window's runs), and the
 * SQL the hot step actually executed in the newest traced run. Selecting a step with sample SQL shows it below
 * the table; the trace link jumps to the full run trace for the whole story.
 */
export function StepsSheet({ pipelineId, flowName, windowDays, onClose }: StepsSheetProps) {
  const navigate = useNavigate();
  const [selectedStep, setSelectedStep] = useState<string | null>(null);
  const query = useQuery({
    queryKey: ["insights", "steps", pipelineId, windowDays],
    queryFn: () => insightsApi.steps(pipelineId!, windowDays, true),
    enabled: pipelineId !== null,
  });

  const steps = query.data?.steps;
  const selected = steps?.find((s) => s.step === selectedStep) ?? null;

  return (
    <Sheet
      open={pipelineId !== null}
      onOpenChange={(open) => {
        if (!open) {
          setSelectedStep(null);
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 overflow-y-auto sm:max-w-2xl" data-testid="insights-steps-sheet">
        <SheetHeader>
          <SheetTitle className="font-mono text-sm">{flowName ?? ""}</SheetTitle>
          <SheetDescription>
            Step cost across the last {windowDays}d of runs. Select a step to see the SQL it ran.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-col gap-3 px-4 pb-4">
          {query.isError ? (
            isApiError(query.error)
              ? <CorrelationError error={query.error} />
              : <p className="text-[13px] text-destructive">{String(query.error)}</p>
          ) : steps === undefined ? (
            <Skeleton className="h-48 rounded-lg" />
          ) : steps.length === 0 ? (
            <EmptyState
              title="No step timings recorded"
              description="This flow's runs did not emit per-step timings in the window. The run trace still lists every statement."
            />
          ) : (
            <DataTable
              columns={stepColumns}
              rows={steps}
              rowKey={(s) => s.step}
              onRowClick={(s) => setSelectedStep(s.step === selectedStep ? null : s.step)}
              rowClickable={(s) => s.sampleSql !== null}
              emptyMessage="No step timings recorded."
              data-testid="insights-steps-table"
            />
          )}
          {selected?.sampleSql != null && (
            <div className="flex flex-col gap-1">
              <div className="text-xs font-medium text-muted-foreground">
                Sample SQL for <span className="font-mono">{selected.step}</span> (newest traced run)
              </div>
              <CodeView value={selected.sampleSql} language="sql" height={220} data-testid="insights-step-sql" />
            </div>
          )}
          {pipelineId !== null && (
            <div>
              <Button
                variant="outline"
                size="sm"
                onClick={() => navigate(`/pipelines/${pipelineId}`)}
                data-testid="insights-steps-open-pipeline"
              >
                Open pipeline
              </Button>
            </div>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
