import { useState } from "react";
import { Link as RouterLink, useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { HeartPulse, ListChecks, Loader2, Network, Play } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { pipelineApi, runApi, scheduleApi } from "../../api/endpoints";
import type { PipelineColumn, PipelineFile, RunSummary, Schedule } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable } from "../../components/DataTable";
import { DetailHeaderCard } from "../../components/DetailHeaderCard";
import { IdChip } from "../../components/IdChip";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge, RunStatusBadge, ScheduleStateBadge } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { embeddedHealthCheckName } from "../../lib/definition";
import { formatBytes, formatDurationSeconds } from "../../lib/time";
import { projectOf } from "../repos/project";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";

/** Definition JSON arrives as one compact string; pretty-print it, falling back to the raw text if malformed. */
function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function scheduleTrigger(row: Schedule): string {
  if (row.cron !== null) {
    return row.cron;
  }

  return row.intervalSeconds !== null ? `every ${row.intervalSeconds}s` : "-";
}

const numeric = (value: number | null | undefined) => (
  <span className="font-mono tabular-nums">{value ?? "-"}</span>
);

const runColumns: Column<RunSummary>[] = [
  { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
  {
    id: "enqueued",
    header: "Enqueued",
    render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
  },
  {
    id: "duration",
    header: "Duration",
    render: (row) => (row.durationSeconds !== null ? formatDurationSeconds(row.durationSeconds) : "-"),
  },
  { id: "rowsLoaded", header: "Rows loaded", align: "right", render: (row) => numeric(row.rowsLoaded) },
  { id: "rowsInserted", header: "Inserted", align: "right", render: (row) => numeric(row.rowsInserted) },
  { id: "rowsUpdated", header: "Updated", align: "right", render: (row) => numeric(row.rowsUpdated) },
  { id: "rowsDeleted", header: "Deleted", align: "right", render: (row) => numeric(row.rowsDeleted) },
  {
    id: "fileCount",
    header: "Files",
    align: "right",
    render: (row) => numeric(row.fileCount > 0 ? row.fileCount : null),
  },
  {
    id: "commit",
    header: "Commit",
    render: (row) => <Mono>{row.commitSha?.slice(0, 10) ?? "-"}</Mono>,
  },
];

const transformColumns: Column<PipelineColumn>[] = [
  {
    id: "kind",
    header: "Kind",
    render: (row) => (
      <Badge
        variant="outline"
        className={row.kind === "declared" ? "border-primary/50 text-primary" : undefined}
      >
        {row.kind}
      </Badge>
    ),
  },
  { id: "ordinal", header: "#", align: "right", render: (row) => numeric(row.ordinal) },
  { id: "column", header: "Column", render: (row) => <Mono>{row.columnName}</Mono> },
  { id: "source", header: "Source column", render: (row) => <Mono>{row.sourceColumn ?? "-"}</Mono> },
  { id: "type", header: "Data type", render: (row) => <Mono>{row.dataType ?? "-"}</Mono> },
  {
    id: "expression",
    header: "Expression",
    render: (row) => <TruncatedText text={row.expression} mono maxWidth={420} />,
  },
  {
    id: "flags",
    header: "Flags",
    render: (row) => (
      <span className="inline-flex flex-wrap items-center gap-1">
        {row.converted && <Badge variant="outline" className="border-success/50 text-success">typed</Badge>}
        {row.isVirtual && <Badge variant="outline">virtual</Badge>}
        {row.excludeFromView && <Badge variant="outline" className="border-warning/50 text-warning">excluded</Badge>}
      </span>
    ),
  },
];

/** The pre-ingestion transform columns of a pipeline: the declared (YAML) and detected (latest run) view
 * projection, fetched whole (a view's column count is bounded by the table it projects). */
function TransformsTab({ pipelineId }: { pipelineId: string }) {
  const columnsQuery = useQuery({
    queryKey: ["pipelines", "columns", pipelineId],
    queryFn: () => pipelineApi.columns(pipelineId),
  });

  if (columnsQuery.isError) {
    return isApiError(columnsQuery.error)
      ? <CorrelationError error={columnsQuery.error} />
      : <p className="text-[13px] text-destructive">{String(columnsQuery.error)}</p>;
  }

  const rows = columnsQuery.data;
  if (rows === undefined) {
    return <Skeleton className="h-60 w-full rounded-lg" data-testid="pipeline-transforms-loading" />;
  }

  if (rows.length === 0) {
    return (
      <Card className="gap-0 rounded-lg p-0">
        <EmptyState
          title="No transformations yet"
          description="Nothing is declared in this pipeline's YAML, and no run has detected any yet."
          data-testid="pipeline-transforms-empty"
        />
      </Card>
    );
  }

  return (
    <DataTable
      columns={transformColumns}
      rows={rows}
      rowKey={(row) => `${row.kind}-${row.ordinal}`}
      emptyMessage="No transformations yet."
      data-testid="pipeline-transforms"
    />
  );
}

const fileColumns: Column<PipelineFile>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => (
      <span className="inline-flex min-w-0 items-center gap-2">
        <Tooltip delayDuration={400}>
          <TooltipTrigger asChild>
            <span className="inline-block max-w-[360px] truncate align-bottom font-mono text-[12px]">
              {row.name}
            </span>
          </TooltipTrigger>
          <TooltipContent side="top" align="start" className="max-w-lg break-all">
            {row.path ?? row.name}
          </TooltipContent>
        </Tooltip>
        {row.lastRun && <Badge variant="outline" className="border-primary/50 text-primary">last run</Badge>}
      </span>
    ),
  },
  { id: "modified", header: "Modified", render: (row) => <RelativeTime value={row.modified} /> },
  {
    id: "rows",
    header: "Rows",
    align: "right",
    render: (row) => numeric(row.rows > 0 ? row.rows : null),
  },
  {
    id: "size",
    header: "Size",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{formatBytes(row.sizeBytes)}</span>,
  },
  { id: "lastProcessed", header: "Last processed", render: (row) => <RelativeTime value={row.lastProcessedUtc} /> },
];

/** Every file this pipeline has processed across its run history, newest-modified first and searchable. Files
 * touched by the pipeline's most recent file-bearing run carry a "last run" badge, so the last run's inputs stand
 * out from everything ever seen. The universe is recorded run history, not a live listing of the source. */
function FilesTab({ pipelineId }: { pipelineId: string }) {
  const [search, setSearch] = useState("");
  return (
    <div className="flex flex-col gap-3">
      <Input
        placeholder="Search files by name or path"
        aria-label="Search files"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        data-testid="pipeline-files-search"
        className="h-8 max-w-sm"
      />
      <PagedTable
        queryKey={["pipelines", "files", pipelineId, search]}
        fetchPage={(page, pageSize) =>
          pipelineApi.files(pipelineId, { search: search.trim() || undefined, page, pageSize })}
        columns={fileColumns}
        rowKey={(row) => `${row.path ?? ""}|${row.name}`}
        emptyMessage="This pipeline has processed no files yet."
        data-testid="pipeline-files-table"
      />
    </div>
  );
}

/**
 * The header's file-size profile: the average size of the files this flow delivers, over its whole recorded run
 * history, with the rest of the profile (count, total, median, spread, extremes, and the newest files' window) in
 * the tooltip. It answers "what does a delivery from here normally weigh" at a glance, so an operator can tell an
 * ordinary load from an outsized or suspiciously thin one without opening the Files tab. A flow that has processed
 * no files (a stored procedure, an export with no recorded output) shows a dash.
 */
function AvgFileSize({ pipelineId }: { pipelineId: string }) {
  const statsQuery = useQuery({
    queryKey: ["pipelines", "file-stats", pipelineId],
    queryFn: () => pipelineApi.fileStats(pipelineId),
    enabled: pipelineId !== "",
  });

  if (statsQuery.isError) {
    return <>-</>;
  }

  const stats = statsQuery.data;
  if (stats === undefined) {
    return <Skeleton className="h-4 w-20" />;
  }

  if (stats.fileCount === 0) {
    return <>-</>;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="font-mono tabular-nums" data-testid="pipeline-avg-file-size">
          {formatBytes(stats.avgBytes)}
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">
        <div className="flex flex-col gap-0.5">
          <span>{`${stats.fileCount.toLocaleString()} file(s), ${formatBytes(stats.totalBytes)} in total`}</span>
          <span>{`Median ${formatBytes(stats.medianBytes)}, spread ±${formatBytes(stats.stdDevBytes)}`}</span>
          <span>{`Smallest ${formatBytes(stats.minBytes)}, largest ${formatBytes(stats.maxBytes)}`}</span>
          {stats.recent !== null && (
            <span>{`Newest ${stats.recent.fileCount} average ${formatBytes(stats.recent.avgBytes)}`}</span>
          )}
          <span>{`${stats.avgRows.toLocaleString()} row(s) per file on average`}</span>
        </div>
      </TooltipContent>
    </Tooltip>
  );
}

const scheduleColumns: Column<Schedule>[] = [
  { id: "trigger", header: "Trigger", render: (row) => <Mono>{scheduleTrigger(row)}</Mono> },
  { id: "timezone", header: "Timezone", render: (row) => row.timezone },
  {
    id: "state",
    header: "State",
    render: (row) => (
      <span className="inline-flex items-center gap-1">
        <ScheduleStateBadge enabled={row.enabled} paused={row.paused} />
        {row.catchup && <Badge variant="outline">catchup</Badge>}
      </span>
    ),
  },
  { id: "source", header: "Source", render: (row) => <Badge variant="outline">{row.source}</Badge> },
  { id: "nextFire", header: "Next fire", render: (row) => <RelativeTime value={row.nextFireUtc} /> },
  { id: "lastFire", header: "Last fire", render: (row) => <RelativeTime value={row.lastFireUtc} /> },
];

/** One pipeline: its definition facts, the YAML and parsed definition, its run history, and its schedules. */
export default function PipelineDetailPage() {
  const { pipelineId = "" } = useParams();
  const navigate = useNavigate();
  const [triggerOpen, setTriggerOpen] = useState(false);

  const detailQuery = useQuery({
    queryKey: ["pipelines", "detail", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
    enabled: pipelineId !== "",
  });

  // The workbench tab reads the pipeline's name once it is known, instead of the generic route title.
  useTabTitle(detailQuery.data?.name);

  // The one-click on-demand executions next to the general trigger dialog: "Run assertions" (ingestion flows;
  // evaluates the flow's declared assertions, manual-mode ones included, against the current target and loads
  // nothing) and "Run health check" (hc flows, or an ing flow's embedded healthCheck: block via its derived
  // flow name; a plain single-flow run, which is also the only way a mode: manual check executes). The new
  // execution is a new run; the button navigates there.
  const triggerFlow = useMutation({
    mutationFn: (parameters: { assertionsOnly?: boolean; flowName?: string }) => {
      const d = detailQuery.data;
      if (!d) {
        throw new Error("The pipeline has not loaded yet.");
      }

      return runApi.trigger({
        repoId: d.repoId,
        flowName: parameters.flowName ?? d.name,
        scope: "flow",
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

  if (detailQuery.isError) {
    return isApiError(detailQuery.error)
      ? <CorrelationError error={detailQuery.error} />
      : <p className="text-[13px] text-destructive">{String(detailQuery.error)}</p>;
  }

  const detail = detailQuery.data;
  if (detail === undefined) {
    return (
      <Page data-testid="page-pipeline-detail">
        <Card className="gap-0 rounded-lg p-4">
          <div className="flex flex-col gap-2">
            <Skeleton className="h-7 w-80" />
            <Skeleton className="h-4 w-[70%]" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        </Card>
        <Skeleton className="h-80 w-full rounded-lg" />
      </Page>
    );
  }

  // An ing document's embedded healthCheck: block derives a sibling hc pipeline; the button triggers it by name.
  const embeddedCheck = detail.kind === "ing" ? embeddedHealthCheckName(detail.definitionJson) : null;

  // Which one-click action is in flight, so only the clicked button wears the spinner.
  const pendingAssertions = triggerFlow.isPending && triggerFlow.variables?.assertionsOnly === true;
  const pendingEmbedded = triggerFlow.isPending && triggerFlow.variables?.flowName !== undefined;
  const pendingHealthCheck = triggerFlow.isPending && !pendingAssertions && !pendingEmbedded;

  return (
    <Page data-testid="page-pipeline-detail">
      <DetailHeaderCard
        title={detail.name}
        badges={(
          <>
            <Badge variant="outline">{detail.kind}</Badge>
            <ActiveBadge active={detail.active} />
            {detail.executionMode === "manual" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Badge
                      variant="secondary"
                      className="bg-warning/15 text-warning"
                      data-testid="pipeline-manual-mode"
                    >
                      manual
                    </Badge>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  mode: manual - excluded from schedules and batch/node group runs; executes only when
                  triggered directly.
                </TooltipContent>
              </Tooltip>
            )}
            {detail.executionMode === "disabled" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Badge
                      variant="secondary"
                      className="bg-destructive/15 text-destructive"
                      data-testid="pipeline-disabled-mode"
                    >
                      disabled
                    </Badge>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  mode: disabled - deactivated: never part of schedules or batch/node group runs (unless the
                  group opts into &quot;find all&quot;); still runnable by a direct trigger.
                </TooltipContent>
              </Tooltip>
            )}
          </>
        )}
        actions={(
          <>
            <Button variant="outline" size="sm" asChild data-testid="pipeline-view-lineage">
              <RouterLink
                to={`/lineage?repoId=${encodeURIComponent(detail.repoId)}&focus=${encodeURIComponent(pipelineId)}`}
              >
                <Network />
                View in lineage
              </RouterLink>
            </Button>
            {detail.kind === "ing" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => triggerFlow.mutate({ assertionsOnly: true })}
                      disabled={triggerFlow.isPending || !detail.active}
                      data-testid="pipeline-run-assertions"
                    >
                      {pendingAssertions ? <Loader2 className="animate-spin" /> : <ListChecks />}
                      Run assertions
                    </Button>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  Evaluate the flow's declared assertions (mode: manual ones included) against the current
                  target; nothing is loaded.
                </TooltipContent>
              </Tooltip>
            )}
            {detail.kind === "hc" && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => triggerFlow.mutate({})}
                      disabled={triggerFlow.isPending || !detail.active}
                      data-testid="pipeline-run-health-check"
                    >
                      {pendingHealthCheck ? <Loader2 className="animate-spin" /> : <HeartPulse />}
                      Run health check
                    </Button>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  Run this health check now. A mode: manual health check executes only from here or a direct
                  trigger.
                </TooltipContent>
              </Tooltip>
            )}
            {embeddedCheck !== null && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => triggerFlow.mutate({ flowName: embeddedCheck })}
                      disabled={triggerFlow.isPending || !detail.active}
                      data-testid="pipeline-run-embedded-health-check"
                    >
                      {pendingEmbedded ? <Loader2 className="animate-spin" /> : <HeartPulse />}
                      Run health check
                    </Button>
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-sm">
                  {`Run this flow's embedded health check ('${embeddedCheck}') against the target now. `
                    + "Embedded checks default to mode: manual, so this button (or a direct trigger) is how "
                    + "they execute."}
                </TooltipContent>
              </Tooltip>
            )}
            <Button size="sm" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
              <Play />
              Trigger run
            </Button>
          </>
        )}
        meta={(
          <>
            <IdChip
              label="repo"
              value={detail.repoId}
              to={`/repos/${detail.repoId}`}
              testId="pipeline-repo-link"
              copyTestId="copy-pipeline-repo"
            />
            <IdChip
              label="hash"
              value={detail.contentHash}
              display={detail.contentHash.slice(0, 10)}
              testId="pipeline-content-hash"
              copyTestId="copy-pipeline-hash"
            />
          </>
        )}
      >
        <DetailPair label="Project">
          <RouterLink
            to={`/repos/${detail.repoId}`}
            className="text-primary hover:underline"
            data-testid="pipeline-project-link"
          >
            {projectOf(detail.relativePath)}
          </RouterLink>
        </DetailPair>
        <DetailPair label="Batch">{detail.batch ?? "-"}</DetailPair>
        <DetailPair label="Wave">{detail.wave === -1 ? "-" : String(detail.wave)}</DetailPair>
        <DetailPair label="Source server"><ConnectionRef value={detail.sourceServer} copyTestId="copy-pipeline-source" /></DetailPair>
        <DetailPair label="Target server"><ConnectionRef value={detail.targetServer} copyTestId="copy-pipeline-target" /></DetailPair>
        <DetailPair label="Path">
          <TruncatedText text={detail.relativePath} mono maxWidth={240} copy copyTestId="copy-pipeline-path" />
        </DetailPair>
        <DetailPair label="Avg file size"><AvgFileSize pipelineId={pipelineId} /></DetailPair>
        <DetailPair label="First seen"><RelativeTime value={detail.firstSeenUtc} absolute /></DetailPair>
        <DetailPair label="Last seen"><RelativeTime value={detail.lastSeenUtc} absolute /></DetailPair>
      </DetailHeaderCard>

      <Tabs defaultValue="yaml">
        <TabsList data-testid="pipeline-tabs">
          <TabsTrigger value="yaml" data-testid="pipeline-tab-yaml">YAML</TabsTrigger>
          <TabsTrigger value="transforms" data-testid="pipeline-tab-transforms">Transforms</TabsTrigger>
          <TabsTrigger value="runs" data-testid="pipeline-tab-runs">Runs</TabsTrigger>
          <TabsTrigger value="files" data-testid="pipeline-tab-files">Files</TabsTrigger>
          <TabsTrigger value="schedules" data-testid="pipeline-tab-schedules">Schedules</TabsTrigger>
          <TabsTrigger value="definition" data-testid="pipeline-tab-definition">Definition</TabsTrigger>
        </TabsList>

        <TabsContent value="yaml">
          <CodeView value={detail.yaml} language="yaml" height={560} lsp data-testid="pipeline-yaml" />
        </TabsContent>
        <TabsContent value="transforms">
          <TransformsTab pipelineId={pipelineId} />
        </TabsContent>
        <TabsContent value="runs">
          <PagedTable
            queryKey={["runs", "by-pipeline", pipelineId]}
            fetchPage={(page, pageSize) => runApi.list({ pipelineId, page, pageSize })}
            columns={runColumns}
            rowKey={(row) => row.runId}
            onRowClick={(row) => navigate(`/runs/${row.runId}`)}
            pollMs={5000}
            emptyMessage="This pipeline has not run yet."
          />
        </TabsContent>
        <TabsContent value="files">
          <FilesTab pipelineId={pipelineId} />
        </TabsContent>
        <TabsContent value="schedules">
          <PagedTable
            queryKey={["schedules", "by-pipeline", pipelineId]}
            fetchPage={(page, pageSize) => scheduleApi.list({ pipelineId, page, pageSize })}
            columns={scheduleColumns}
            rowKey={(row) => row.id}
            emptyMessage="This pipeline has no schedules."
          />
        </TabsContent>
        <TabsContent value="definition">
          <CodeView
            value={prettyJson(detail.definitionJson)}
            language="json"
            height={560}
            data-testid="pipeline-definition"
          />
        </TabsContent>
      </Tabs>

      {triggerOpen && (
        <TriggerRunDialog
          open
          onClose={() => setTriggerOpen(false)}
          repoId={detail.repoId}
          flowName={detail.name}
          flowId={detail.id}
        />
      )}
    </Page>
  );
}
