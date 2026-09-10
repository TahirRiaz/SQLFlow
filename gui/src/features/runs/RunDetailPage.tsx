import { useCallback, useEffect, useRef, useState, type CSSProperties } from "react";
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CircleAlert, HeartPulse, Info, ListChecks, Loader2, RotateCcw, Terminal } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Sheet, SheetContent, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import type {
  RunAssertion, RunFile, RunHealthCheckMetric, RunStatement, RunSurrogateKey,
} from "../../api/types";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { DetailPair } from "../../components/DetailPair";
import { IdChip } from "../../components/IdChip";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { pollingInterval } from "../../hooks/usePolling";
import { usePanel } from "../../layout/workbench/PanelContext";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { embeddedHealthCheckName } from "../../lib/definition";
import { formatBytes, formatDurationSeconds, parseUtc } from "../../lib/time";
import { RunTracePanel } from "./RunTracePanel";
import { TriggerRunDialog } from "./TriggerRunDialog";

/** A compact UTC stamp for a backfill window bound (the API sends UTC timestamps). */
function fmtBound(value: string): string {
  return parseUtc(value).toISOString().replace("T", " ").replace(/:\d\d\.\d+Z$/, "");
}

/** A soft red wash marking a failed row (the one statement that threw). */
const failedRowStyle: CSSProperties = {
  backgroundColor: "color-mix(in srgb, var(--destructive) 12%, transparent)",
};

/** The badge for an incremental mode: an incremental read is the notable case (primary), a full read is
 * neutral, and the operator-driven backfill / init-load modes echo the backfill banner's warning tone. */
function IncrementalModeBadge({ mode }: { mode: string }) {
  if (mode === "incremental") {
    return <Badge>{mode}</Badge>;
  }

  if (mode === "backfill" || mode === "init-load") {
    return <Badge variant="secondary" className="bg-warning/15 text-warning">{mode}</Badge>;
  }

  return <Badge variant="secondary">{mode}</Badge>;
}

/** A row counter: grouped for readability, a real zero kept distinct from an unrecorded value (muted dash). */
function RowCount({ value }: { value: number | null }) {
  if (value == null) {
    return <span className="text-muted-foreground">-</span>;
  }

  return <span className="font-mono tabular-nums">{value.toLocaleString("en-US")}</span>;
}

function YesNo({ value }: { value: boolean }) {
  return value
    ? <Badge variant="outline" className="border-success/40 text-success">yes</Badge>
    : <Badge variant="outline" className="text-muted-foreground">no</Badge>;
}

/** A step name marked as failed: the error icon plus destructive text, so color never carries it alone. */
function FailedStep({ step }: { step: string | null }) {
  return (
    <span className="inline-flex items-center gap-1.5 font-semibold text-destructive">
      <CircleAlert className="size-4 shrink-0" />
      {step ?? "-"}
    </span>
  );
}

const statementColumns: Column<RunStatement>[] = [
  { id: "ordinal", header: "Ordinal", width: 90, render: (row) => row.ordinal },
  {
    id: "step",
    header: "Step",
    width: 220,
    render: (row) => (row.error ? <FailedStep step={row.step} /> : row.step),
  },
  {
    id: "sql",
    header: "SQL",
    render: (row) => <TruncatedText text={row.sql} mono maxWidth={640} />,
  },
];

const assertionColumns: Column<RunAssertion>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "result", header: "Result", render: (row) => row.result },
  { id: "assertedValue", header: "Asserted value", render: (row) => <TruncatedText text={row.assertedValue} maxWidth={280} /> },
  { id: "evaluated", header: "Evaluated", render: (row) => <YesNo value={row.evaluated} /> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

const fileColumns: Column<RunFile>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "path", header: "Path", render: (row) => <TruncatedText text={row.path} mono maxWidth={520} /> },
  { id: "rows", header: "Rows", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.rows}</span> },
  { id: "columns", header: "Columns", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.columns}</span> },
  { id: "size", header: "Size", align: "right", render: (row) => <span className="font-mono tabular-nums">{formatBytes(row.sizeBytes)}</span> },
  {
    id: "hash",
    header: "Hash",
    render: (row) => (row.hash ? <TruncatedText text={row.hash} mono maxWidth={140} /> : "-"),
  },
];

const surrogateKeyColumns: Column<RunSurrogateKey>[] = [
  { id: "surrogateTable", header: "Table", render: (row) => row.surrogateTable },
  { id: "surrogateColumn", header: "Column", render: (row) => row.surrogateColumn },
  { id: "keysGenerated", header: "Keys generated", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.keysGenerated}</span> },
  { id: "rowsStamped", header: "Rows stamped", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.rowsStamped}</span> },
  { id: "isRemote", header: "Remote", render: (row) => <YesNo value={row.isRemote} /> },
  { id: "executed", header: "Executed", render: (row) => <YesNo value={row.executed} /> },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

const healthMetricColumns: Column<RunHealthCheckMetric>[] = [
  { id: "name", header: "Name", render: (row) => row.name },
  { id: "seriesPoints", header: "Series points", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.seriesPoints}</span> },
  { id: "imputedPoints", header: "Imputed", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.imputedPoints}</span> },
  { id: "immaturePoints", header: "Immature", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.immaturePoints}</span> },
  { id: "anomalies", header: "Anomalies", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.anomalies}</span> },
  { id: "levelShifts", header: "Level shifts", align: "right", render: (row) => <span className="font-mono tabular-nums">{row.levelShifts}</span> },
  { id: "modelTrained", header: "Model trained", render: (row) => <YesNo value={row.modelTrained} /> },
  { id: "modelTrainer", header: "Trainer", render: (row) => row.modelTrainer ?? "-" },
  { id: "error", header: "Error", render: (row) => <TruncatedText text={row.error} maxWidth={360} /> },
];

/** Everything the API recorded about one run: live header while active, detail tables once results land. */
export default function RunDetailPage() {
  const { runId } = useParams<{ runId: string }>();
  if (!runId) {
    return <Navigate to="/runs" replace />;
  }

  return <RunDetailContent runId={runId} />;
}

function RunDetailContent({ runId }: { runId: string }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const panel = usePanel();
  const [tab, setTab] = useState("source");
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [statement, setStatement] = useState<RunStatement | null>(null);

  const query = useQuery({
    queryKey: ["runs", runId],
    queryFn: () => runApi.getById(runId),
    refetchInterval: (q) => {
      const data = q.state.data;
      if (data && (data.status === "succeeded" || data.status === "failed" || data.status === "cancelled")) {
        return false;
      }

      return pollingInterval(3000)();
    },
  });
  useTabTitle(query.data?.flowName);

  // The pipeline's stored definition serves two tabs: for an ingestion run it says whether the flow embeds a
  // healthCheck: block (its derived flow name powers the Health tab's "Run health check" button, since the check
  // is a sibling pipeline whose metrics live on its own runs), and for every run it backs the Source tab's flow
  // YAML. Fetched for ing runs eagerly (the embedded-check name is needed before the Health tab opens) and for
  // any run once the Source tab is selected, so a non-ing run pays for it only when the source is actually read.
  // Cached under the same key the pipeline detail page uses.
  const pipelineQuery = useQuery({
    queryKey: ["pipelines", "detail", query.data?.pipelineId ?? ""],
    queryFn: () => pipelineApi.getById(query.data!.pipelineId),
    enabled: query.data !== undefined && (query.data.flowKind === "ing" || tab === "source"),
  });
  const embeddedCheck = pipelineQuery.data ? embeddedHealthCheckName(pipelineQuery.data.definitionJson) : null;

  // Once the run reaches a terminal status, refetch everything under it so every tab shows the authoritative
  // re-projected result without a manual reload (the header poll observes the transition).
  const status = query.data?.status;
  const live = status === "queued" || status === "running";
  const refreshRun = useCallback(
    () => void queryClient.invalidateQueries({ queryKey: ["runs", runId] }),
    [queryClient, runId],
  );
  const wasLive = useRef(false);
  useEffect(() => {
    if (live) {
      wasLive.current = true;
    } else if (wasLive.current) {
      wasLive.current = false;
      refreshRun();
    }
  }, [live, refreshRun]);

  // The trace lives in the workbench bottom panel (the "trace window"), not an embedded tab: opening a run raises
  // it there and it streams live while the run executes, exactly like the repository sync trace. Opened once per
  // run, when the flow name is known so the panel is titled; the header's Trace button reopens it if closed.
  const flowName = query.data?.flowName;
  const openTrace = useCallback(() => {
    panel.open({
      id: `run-trace:${runId}`,
      title: `Trace · ${flowName ?? runId}`,
      node: <RunTracePanel runId={runId} />,
    });
  }, [panel, runId, flowName]);
  const openedFor = useRef<string | null>(null);
  useEffect(() => {
    if (openedFor.current === runId || flowName === undefined) {
      return;
    }

    openedFor.current = runId;
    openTrace();
  }, [runId, flowName, openTrace]);

  // Re-run opens the trigger sheet prefilled with this run's flow and the operator parameters it carried (full
  // load, backfill window, file pattern), so the run can be repeated as-is or adjusted before launching. The sheet
  // renders exactly the parameters this flow's kind honors and navigates to the new run on submit.
  const [rerunOpen, setRerunOpen] = useState(false);

  // The on-demand execution the Assertions and Health tabs offer: a fresh single-flow run of this run's
  // pipeline against the current code. With assertionsOnly the engine evaluates the flow's declared assertions
  // (manual-mode ones included) against the current target and loads nothing; without it, it is a plain run
  // (the Health tab's "Run health check" for hc flows). The new execution is a new run; the button navigates there.
  const triggerFlow = useMutation({
    mutationFn: (parameters: { assertionsOnly?: boolean; flowName?: string }) => {
      const r = query.data;
      if (!r?.repoId) {
        throw new Error("The run has not loaded yet.");
      }

      return runApi.trigger({
        repoId: r.repoId,
        flowName: parameters.flowName ?? r.flowName,
        scope: "flow",
        pool: r.targetPool,
        assertionsOnly: parameters.assertionsOnly,
      });
    },
    onSuccess: (accepted, parameters) => {
      toast.success(parameters.assertionsOnly ? "Assertion run enqueued." : "Health check enqueued.");
      if (accepted.runId) {
        navigate(`/runs/${accepted.runId}`);
      }
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const cancel = useMutation({
    mutationFn: () => runApi.cancel(runId),
    onSuccess: () => {
      toast.success("Cancel requested for this run.");
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["runs", runId] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  if (query.isError) {
    return (
      <Page data-testid="page-run-detail">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const run = query.data;
  if (run === undefined) {
    return (
      <Page data-testid="page-run-detail">
        <Skeleton className="h-60 w-full rounded-lg" />
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  // A run can be cancelled while queued (dequeued outright) or while running (the node aborts the in-flight
  // statement). Once a running run's cancel is in flight, the button is disabled and a "cancelling" badge shows,
  // until polling reflects the terminal "cancelled" status.
  const cancellable = run.status === "queued" || run.status === "running";
  const cancelling = run.status === "running" && run.cancelRequestedUtc !== null;

  return (
    <Page data-testid="page-run-detail">
      {/* Why it failed comes first: on a failed run that is the only thing being looked for, and behind a full
          header card of timings and row counts it was the last thing read. */}
      {run.status === "failed" && run.error !== null && (
        <Alert variant="destructive" data-testid="run-error">
          <CircleAlert />
          <AlertDescription>{run.error}</AlertDescription>
        </Alert>
      )}

      <DetailHeaderCard
        title={run.flowName}
        badges={(
          <>
            <RunStatusBadge status={run.status} />
            {cancelling && (
              <Badge variant="secondary" className="bg-warning/15 text-warning" data-testid="run-cancelling">
                cancelling
              </Badge>
            )}
            <Badge variant="outline" data-testid="run-kind">{run.flowKind}</Badge>
            <Badge variant="outline" data-testid="run-batch">batch: {run.batch}</Badge>
            {run.assertionsOnly && (
              <Badge variant="secondary" className="bg-info/12 text-info" data-testid="run-assertions-only">
                assertions only
              </Badge>
            )}
          </>
        )}
        actions={(
          <>
            <Button variant="outline" size="sm" onClick={openTrace} data-testid="open-run-trace">
              <Terminal />
              Trace
            </Button>
            {cancellable
              ? (
                <Button
                  variant="outline"
                  size="sm"
                  className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                  onClick={() => setConfirmOpen(true)}
                  disabled={cancelling || cancel.isPending}
                  data-testid="cancel-run"
                >
                  {cancelling ? "Cancelling..." : "Cancel run"}
                </Button>
              )
              : (run.repoId !== null && (
                <Button variant="outline" size="sm" onClick={() => setRerunOpen(true)} data-testid="rerun-run">
                  <RotateCcw />
                  Re-run
                </Button>
              ))}
          </>
        )}
        meta={(
          <>
            <IdChip label="run" value={run.runId} testId="run-id" copyTestId="copy-run-id" />
            {run.repoId && (
              <IdChip label="repo" value={run.repoId} to={`/repos/${run.repoId}`} testId="run-repo-link" copyTestId="copy-run-repo" />
            )}
            <IdChip label="pipeline" value={run.pipelineId} to={`/pipelines/${run.pipelineId}`} testId="run-pipeline-link" copyTestId="copy-run-pipeline" />
            {run.groupId && (
              <IdChip label="group" value={run.groupId} to={`/runs/groups/${run.groupId}`} testId="run-group-link" copyTestId="copy-run-group" />
            )}
            {run.commitSha && (
              <IdChip label="commit" value={run.commitSha} display={run.commitSha.slice(0, 7)} testId="run-commit" copyTestId="copy-run-commit" />
            )}
          </>
        )}
      >
        <DetailPair label="Enqueued"><RelativeTime value={run.enqueuedUtc} absolute /></DetailPair>
        <DetailPair label="Started"><RelativeTime value={run.startUtc} absolute /></DetailPair>
        <DetailPair label="Ended"><RelativeTime value={run.endUtc} absolute /></DetailPair>
        <DetailPair label="Duration">
          <span className="font-mono tabular-nums">
            {run.durationSeconds != null ? formatDurationSeconds(run.durationSeconds) : "-"}
          </span>
        </DetailPair>
        <DetailPair label="Rows loaded"><RowCount value={run.rowsLoaded} /></DetailPair>
        <DetailPair label="Rows inserted"><RowCount value={run.rowsInserted} /></DetailPair>
        <DetailPair label="Rows updated"><RowCount value={run.rowsUpdated} /></DetailPair>
        <DetailPair label="Rows deleted"><RowCount value={run.rowsDeleted} /></DetailPair>
        <DetailPair label="Step">
          <span className="font-mono tabular-nums">{run.wave >= 0 ? run.wave : "-"}</span>
        </DetailPair>
        <DetailPair label="Target pool">{run.targetPool ?? "-"}</DetailPair>
        <DetailPair label="Node"><span className="break-all font-mono text-[12px]">{run.claimedByNode ?? "-"}</span></DetailPair>
        {/* Host repeats the node name on a single-container node; surface it only when it actually adds a value. */}
        {run.host && run.host !== run.claimedByNode && (
          <DetailPair label="Host"><span className="break-all font-mono text-[12px]">{run.host}</span></DetailPair>
        )}
      </DetailHeaderCard>

      {run.status === "failed" && run.failedStatementSql !== null && (
        <Card className="gap-2 rounded-lg border-destructive/50 p-3" data-testid="run-failed-statement">
          <h2 className="text-[13px] font-medium text-destructive">
            Failing statement {run.failedStatementOrdinal}: {run.failedStatementStep}
          </h2>
          <CodeView value={run.failedStatementSql} language="sql" data-testid="run-error-sql" />
        </Card>
      )}

      {run.assertionsOnly && (
        <Alert data-testid="run-assertions-only-banner">
          <Info />
          <AlertDescription>
            Assertions-only run: the flow's declared assertions (manual-mode ones included) were evaluated against
            the current target; no data was read or loaded.
          </AlertDescription>
        </Alert>
      )}

      {(run.fullLoad || run.backfillFrom || run.filePattern || run.sourceFilter) && (
        <Alert data-testid="run-backfill">
          <Info />
          <AlertDescription>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-foreground">Backfill:</span>
              {run.fullLoad && <Badge variant="secondary" className="bg-warning/15 text-warning">full load</Badge>}
              {run.backfillFrom && (
                <Badge variant="secondary" className="font-mono">
                  {run.backfillTo
                    ? `window ${fmtBound(run.backfillFrom)} .. ${fmtBound(run.backfillTo)}`
                    : `from ${fmtBound(run.backfillFrom)}`}
                </Badge>
              )}
              {run.filePattern && <Badge variant="secondary" className="font-mono">files '{run.filePattern}'</Badge>}
              {run.sourceFilter && (
                <Badge variant="secondary" className="font-mono">source filter '{run.sourceFilter}'</Badge>
              )}
            </div>
          </AlertDescription>
        </Alert>
      )}

      {run.incrementalMode && (
        <Card className="gap-2 rounded-lg p-3" data-testid="run-incremental">
          <div className="flex items-center gap-2">
            <h2 className="text-[13px] font-medium">Incremental load</h2>
            <IncrementalModeBadge mode={run.incrementalMode} />
          </div>
          <div className="flex flex-col gap-2">
            {run.incrementalFilter && (
              <DetailPair label="Filter"><Mono>{run.incrementalFilter}</Mono></DetailPair>
            )}
            {run.incrementalWatermark && (
              <DetailPair label="Watermark"><Mono>{run.incrementalWatermark}</Mono></DetailPair>
            )}
            {run.incrementalWatermarkSource && (
              <DetailPair label="Watermark source"><Mono>{run.incrementalWatermarkSource}</Mono></DetailPair>
            )}
          </div>
        </Card>
      )}

      {run.dataSetConvention && (
        <Card className="gap-2 rounded-lg p-3" data-testid="run-dataset-convention">
          <h2 className="text-[13px] font-medium">DataSet date detection</h2>
          <DetailPair label="Convention"><Mono>{run.dataSetConvention}</Mono></DetailPair>
        </Card>
      )}

      <Tabs value={tab} onValueChange={setTab} className="gap-4">
        <TabsList variant="line" className="w-full justify-start overflow-x-auto" data-testid="run-tabs">
          <TabsTrigger value="source" className="flex-none" data-testid="tab-source">Source</TabsTrigger>
          <TabsTrigger value="files" className="flex-none" data-testid="tab-files">Files</TabsTrigger>
          <TabsTrigger value="statements" className="flex-none" data-testid="tab-statements">Statements</TabsTrigger>
          <TabsTrigger value="surrogate-keys" className="flex-none" data-testid="tab-surrogate-keys">Surrogate</TabsTrigger>
          <TabsTrigger value="assertions" className="flex-none" data-testid="tab-assertions">Assertions</TabsTrigger>
          <TabsTrigger value="health-metrics" className="flex-none" data-testid="tab-health-metrics">Health</TabsTrigger>
        </TabsList>

        <TabsContent value="source" className="flex flex-col gap-2">
          <p className="text-[13px] text-muted-foreground">
            The flow's YAML definition as registered in the catalog. This is the current source for this pipeline;
            {run.commitSha
              ? ` the run executed against commit ${run.commitSha.slice(0, 12)}, so a later change to the flow file may differ from what ran.`
              : " if the flow file has changed since this run, the executed source may differ."}
          </p>
          {pipelineQuery.isError && (
            <Alert variant="destructive" data-testid="source-error">
              <CircleAlert />
              <AlertDescription>
                {isApiError(pipelineQuery.error)
                  ? pipelineQuery.error.title
                  : "Could not load the flow definition from the catalog."}
              </AlertDescription>
            </Alert>
          )}
          {pipelineQuery.data === undefined && !pipelineQuery.isError
            ? <Skeleton className="h-[560px] w-full rounded-lg" data-testid="source-loading" />
            : pipelineQuery.data !== undefined && (
              <CodeView value={pipelineQuery.data.yaml} language="yaml" height={560} lsp data-testid="run-source-yaml" />
            )}
        </TabsContent>
        <TabsContent value="files">
          <PagedTable
            queryKey={["runs", runId, "files"]}
            fetchPage={(page, pageSize) => runApi.files(runId, { page, pageSize })}
            columns={fileColumns}
            rowKey={(row) => row.id}
            emptyMessage="No files were recorded for this run."
            data-testid="files-table"
          />
        </TabsContent>
        <TabsContent value="statements">
          <PagedTable
            queryKey={["runs", runId, "statements"]}
            fetchPage={(page, pageSize) => runApi.statements(runId, { page, pageSize })}
            columns={statementColumns}
            rowKey={(row) => row.id}
            onRowClick={(row) => setStatement(row)}
            // While the run is executing, the node streams each statement into the catalog as it runs, so poll to
            // show them live; polling stops once the run reaches a terminal state.
            pollMs={run.status === "running" ? 3000 : undefined}
            rowSx={(row) => (row.error ? failedRowStyle : undefined)}
            emptyMessage={run.status === "running"
              ? "Statements will appear here as the run executes."
              : "No statements were recorded for this run."}
            data-testid="statements-table"
          />
        </TabsContent>
        <TabsContent value="surrogate-keys">
          <PagedTable
            queryKey={["runs", runId, "surrogate-keys"]}
            fetchPage={(page, pageSize) => runApi.surrogateKeys(runId, { page, pageSize })}
            columns={surrogateKeyColumns}
            rowKey={(row) => row.id}
            emptyMessage="No surrogate keys were recorded for this run."
            data-testid="surrogate-keys-table"
          />
        </TabsContent>
        <TabsContent value="assertions" className="flex flex-col gap-2">
          {run.flowKind === "ing" && run.repoId !== null && (
            <div className="flex flex-wrap items-center gap-2">
              <p className="text-[13px] text-muted-foreground">
                Run the flow's declared assertions on demand, including mode: manual ones; nothing is loaded.
              </p>
              <div className="grow" />
              <Button
                variant="outline"
                size="sm"
                onClick={() => triggerFlow.mutate({ assertionsOnly: true })}
                disabled={triggerFlow.isPending}
                data-testid="run-assertions"
              >
                {triggerFlow.isPending ? <Loader2 className="animate-spin" /> : <ListChecks />}
                Run assertions
              </Button>
            </div>
          )}
          <PagedTable
            queryKey={["runs", runId, "assertions"]}
            fetchPage={(page, pageSize) => runApi.assertions(runId, { page, pageSize })}
            columns={assertionColumns}
            rowKey={(row) => row.id}
            emptyMessage={run.flowKind === "ing"
              ? "No assertions were recorded for this run. Auto-mode assertions run with every load; manual-mode ones only in an assertions-only run."
              : "No assertions were recorded for this run."}
            data-testid="assertions-table"
          />
        </TabsContent>
        <TabsContent value="health-metrics" className="flex flex-col gap-2">
          {run.flowKind === "hc" && run.repoId !== null && (
            <div className="flex flex-wrap items-center gap-2">
              <p className="text-[13px] text-muted-foreground">
                Run this health check on demand; manual-mode (mode: manual) checks execute only from here or a direct trigger.
              </p>
              <div className="grow" />
              <Button
                variant="outline"
                size="sm"
                onClick={() => triggerFlow.mutate({})}
                disabled={triggerFlow.isPending}
                data-testid="run-health-check"
              >
                {triggerFlow.isPending ? <Loader2 className="animate-spin" /> : <HeartPulse />}
                Run health check
              </Button>
            </div>
          )}
          {run.flowKind === "ing" && embeddedCheck !== null && run.repoId !== null && (
            <div className="flex flex-wrap items-center gap-2">
              <p className="text-[13px] text-muted-foreground">
                This flow embeds health check '{embeddedCheck}'; it runs on demand and records its metrics on its own runs.
              </p>
              <div className="grow" />
              <Button
                variant="outline"
                size="sm"
                onClick={() => triggerFlow.mutate({ flowName: embeddedCheck })}
                disabled={triggerFlow.isPending}
                data-testid="run-embedded-health-check"
              >
                {triggerFlow.isPending ? <Loader2 className="animate-spin" /> : <HeartPulse />}
                Run health check
              </Button>
            </div>
          )}
          <PagedTable
            queryKey={["runs", runId, "health-metrics"]}
            fetchPage={(page, pageSize) => runApi.healthMetrics(runId, { page, pageSize })}
            columns={healthMetricColumns}
            rowKey={(row) => row.id}
            emptyMessage={run.flowKind === "hc"
              ? "No health metrics were recorded for this run."
              : run.flowKind === "ing" && embeddedCheck !== null
                ? `Health metrics are recorded on the embedded check's own runs ('${embeddedCheck}'); use Run health check to execute it now.`
                : `Health metrics are produced by health-check (hc) flows; this is a '${run.flowKind}' run.`}
            data-testid="health-metrics-table"
          />
        </TabsContent>
      </Tabs>

      <Sheet
        open={statement !== null}
        onOpenChange={(next) => {
          if (!next) {
            setStatement(null);
          }
        }}
      >
        {/* Focus stays outside Monaco so Escape reaches the sheet, not the editor. */}
        <SheetContent className="w-full gap-0 sm:max-w-3xl" aria-describedby={undefined} data-testid="statement-dialog" onOpenAutoFocus={(event) => event.preventDefault()}>
          <SheetHeader>
            <SheetTitle>{statement ? `Statement ${statement.ordinal}: ${statement.step}` : ""}</SheetTitle>
          </SheetHeader>
          <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
            {statement?.error && (
              <Alert variant="destructive" data-testid="statement-error">
                <CircleAlert />
                <AlertDescription>{statement.error}</AlertDescription>
              </Alert>
            )}
            {statement && <CodeView value={statement.sql} language="sql" data-testid="statement-sql" />}
          </div>
        </SheetContent>
      </Sheet>

      <ConfirmDialog
        open={confirmOpen}
        title="Cancel run"
        message={run.status === "running"
          ? `Cancel running run ${run.runId}? The node executing it aborts the in-flight statement and rolls back its transaction.`
          : `Cancel queued run ${run.runId}? A queued run is dequeued before any node claims it.`}
        confirmLabel="Cancel run"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
      {run.repoId !== null && (
        <TriggerRunDialog
          open={rerunOpen}
          onClose={() => setRerunOpen(false)}
          repoId={run.repoId}
          flowName={run.flowName}
          flowId={run.pipelineId}
          initialParameters={{
            fullLoad: run.fullLoad,
            backfillFrom: run.backfillFrom,
            backfillTo: run.backfillTo,
            filePattern: run.filePattern,
            sourceFilter: run.sourceFilter,
            assertionsOnly: run.assertionsOnly,
          }}
        />
      )}
    </Page>
  );
}
