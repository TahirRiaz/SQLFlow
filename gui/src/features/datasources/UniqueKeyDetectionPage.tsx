import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Ban, CircleAlert, Info, KeyRound, Loader2, Lock, X,
} from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import {
  Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { datasourceApi } from "../../api/endpoints";
import type {
  ComputeTask, ComputeTaskSummary, DatasourceDatabase, DatasourceObject, DatasourceObjectPage,
  DatasourceSchema, UniqueKeyReport,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ComboBoxField } from "../../components/ComboBoxField";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { parseUtc } from "../../lib/time";
import { UniqueKeyReportView } from "./UniqueKeyReportView";
import { useCompute } from "./useCompute";

/** Only these provider kinds can be profiled (detection is T-SQL); null means "unknown, assume SQL Server". */
function detectableKind(kind: string | null): boolean {
  return kind === null || kind === "MSSQL" || kind === "AZDB";
}

/** "1m 23s" from two UTC instants; a dash while either end is missing. */
function durationLabel(startUtc: string | null, endUtc: string | null): string {
  if (startUtc === null || endUtc === null) {
    return "-";
  }

  const totalSeconds = Math.max(0, Math.round((parseUtc(endUtc).getTime() - parseUtc(startUtc).getTime()) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  return minutes > 0 ? `${minutes}m ${totalSeconds % 60}s` : `${totalSeconds}s`;
}

/** "0:42" elapsed-time ticker text. */
function elapsedLabel(sinceMs: number, nowMs: number): string {
  const totalSeconds = Math.max(0, Math.floor((nowMs - sinceMs) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  return `${minutes}:${String(totalSeconds % 60).padStart(2, "0")}`;
}

/**
 * A past detection task opened from the history list: the stored report for a succeeded task, the error for a
 * failed one, live progress otherwise (the sheet polls until the task is terminal, so an operator can follow
 * a detection another session started).
 */
function HistoryTaskSheet({ taskId, onClose }: { taskId: string; onClose: () => void }) {
  const task = useQuery({
    queryKey: ["compute-task", taskId],
    queryFn: () => datasourceApi.task(taskId),
    refetchInterval: (query) => {
      const status = (query.state.data as ComputeTask | undefined)?.status;
      return status === "queued" || status === "running" ? 2000 : false;
    },
  });

  const data = task.data;
  const report = data?.status === "succeeded" && data.result !== null ? (data.result as UniqueKeyReport) : null;

  return (
    <Sheet open onOpenChange={(open) => { if (!open) onClose(); }}>
      <SheetContent
        side="right"
        className="w-full gap-0 sm:max-w-2xl"
        showCloseButton={false}
        data-testid="history-task-dialog"
      >
        <SheetHeader className="border-b border-border">
          <div className="flex items-center justify-between gap-2">
            <SheetTitle className="flex min-w-0 flex-wrap items-center gap-2 text-base">
              <span>Detection report</span>
              {data?.target != null && <Mono className="text-[13px] font-semibold">{data.target}</Mono>}
              {data !== undefined && <RunStatusBadge status={data.status} />}
            </SheetTitle>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Close"
              onClick={onClose}
              data-testid="history-task-close"
            >
              <X />
            </Button>
          </div>
          <SheetDescription className="sr-only">
            The stored unique key detection report for one past compute task.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto p-4">
          {task.isPending && (
            <div className="flex flex-col gap-2">
              <Skeleton className="h-4 w-3/4" />
              <Skeleton className="h-4 w-1/2" />
              <Skeleton className="h-24 w-full" />
            </div>
          )}
          {task.isError && (isApiError(task.error)
            ? <CorrelationError error={task.error} />
            : <p className="text-[13px] text-destructive">{String(task.error)}</p>)}
          {data !== undefined && (
            <p className="text-[13px] text-muted-foreground">
              <Mono>{data.sourceRef}</Mono>, enqueued <RelativeTime value={data.enqueuedUtc} />
              {data.endUtc !== null ? <>, ran {durationLabel(data.startUtc, data.endUtc)}</> : null}
              {data.requestedBy !== null ? <>, by {data.requestedBy}</> : null}
            </p>
          )}
          {data !== undefined && (data.status === "queued" || data.status === "running") && (
            <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 animate-spin" />
              The task is {data.status}; this panel follows it live.
            </div>
          )}
          {data?.error != null && (
            <Alert variant="destructive" data-testid="history-task-error">
              <CircleAlert />
              <AlertDescription>{data.error}</AlertDescription>
            </Alert>
          )}
          {report !== null && <UniqueKeyReportView report={report} data-testid="history-task-report" />}
        </div>
      </SheetContent>
    </Sheet>
  );
}

/**
 * The dedicated unique-key detection surface: pick any table or view reachable through a datasource
 * reference, tune the search (sampling, key width, whether declared keys answer from metadata), run the
 * detection as a durable compute task on a worker node, and read the full report: ranked candidates with
 * selectivity, per-column statistics, excluded columns, notes. Past detections stay on the durable task queue
 * and reopen from the history list. The scope lives in the URL, so a detection setup is shareable and the
 * inspector sheet deep-links here.
 */
export default function UniqueKeyDetectionPage() {
  const [params, setParams] = useSearchParams();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  const reference = params.get("ref") ?? "";
  const kind = params.get("kind");
  const database = params.get("db");
  const schema = params.get("schema");
  const objectName = params.get("object") ?? "";

  /** Rewrites the URL scope in place; empty values drop their parameter so links stay minimal. */
  const setScope = useCallback((patch: Record<string, string | null>) => {
    setParams((current) => {
      const next = new URLSearchParams(current);
      for (const [key, value] of Object.entries(patch)) {
        if (value === null || value === "") {
          next.delete(key);
        } else {
          next.set(key, value);
        }
      }

      return next;
    }, { replace: true });
  }, [setParams]);

  // ---- Scope pickers (each level is a live compute task, like the datasource browser) ----------------------
  const datasources = useQuery({ queryKey: ["datasources", "list"], queryFn: () => datasourceApi.list() });
  const databases = useCompute<{ databases: DatasourceDatabase[] }>();
  const schemas = useCompute<{ schemas: DatasourceSchema[] }>();
  const objects = useCompute<DatasourceObjectPage>();
  const { run: runDatabases } = databases;
  const { run: runSchemas } = schemas;
  const { run: runObjects } = objects;

  const [objectInput, setObjectInput] = useState("");
  const usable = reference !== "" && canOperate;

  useEffect(() => {
    if (usable) {
      void runDatabases({ reference, kind, operation: "listDatabases" });
    }
  }, [usable, runDatabases, reference, kind]);

  useEffect(() => {
    if (usable) {
      void runSchemas({ reference, kind, database, operation: "listSchemas" });
    }
  }, [usable, runSchemas, reference, kind, database]);

  // The object picker searches as you type (debounced), scoped to the chosen database/schema.
  useEffect(() => {
    if (!usable) {
      return;
    }

    const handle = setTimeout(() => {
      void runObjects({
        reference, kind, database, schema,
        nameLike: objectInput.trim() === "" ? null : objectInput.trim(),
        limit: 200,
        operation: "listObjects",
      });
    }, 350);
    return () => clearTimeout(handle);
  }, [usable, runObjects, reference, kind, database, schema, objectInput]);

  const selectableSources = useMemo(
    () => (datasources.data ?? []).filter((d) => d.resolvable && detectableKind(d.kind)),
    [datasources.data],
  );
  const selectedObject: DatasourceObject | null = objectName !== "" && schema !== null
    ? { schema, name: objectName, type: "Table", approxRows: 0 }
    : null;

  // ---- Options ----------------------------------------------------------------------------------------------
  const [sampleMode, setSampleMode] = useState<"auto" | "full" | "custom">("auto");
  const [sampleSize, setSampleSize] = useState(500_000);
  const [maxKeyColumns, setMaxKeyColumns] = useState(4);
  const [maxCandidates, setMaxCandidates] = useState(5);
  const [verify, setVerify] = useState(true);
  const [trustDeclaredKeys, setTrustDeclaredKeys] = useState(true);

  const sampleInvalid = sampleMode === "custom" && (!Number.isFinite(sampleSize) || sampleSize < 1);
  const widthInvalid = !Number.isFinite(maxKeyColumns) || maxKeyColumns < 1 || maxKeyColumns > 16;
  const candidatesInvalid = !Number.isFinite(maxCandidates) || maxCandidates < 1 || maxCandidates > 50;

  // ---- Run --------------------------------------------------------------------------------------------------
  const detect = useCompute<UniqueKeyReport>();
  const [startedAtMs, setStartedAtMs] = useState<number | null>(null);
  const [nowMs, setNowMs] = useState(0);
  const [cancelled, setCancelled] = useState(false);
  const [historyVersion, setHistoryVersion] = useState(0);
  const [openTaskId, setOpenTaskId] = useState<string | null>(null);

  useEffect(() => {
    if (!detect.running) {
      return;
    }

    const timer = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [detect.running]);

  // The terminal failure toast (DESIGN.md 8.2). A cancel (button or navigation) aborts the long-poll before
  // any error lands, so it never toasts; the kd-cancelled notice below covers that path instead.
  useEffect(() => {
    if (detect.error !== null) {
      toast.error(detect.error);
    }
  }, [detect.error]);

  const canRun = usable && schema !== null && objectName !== ""
    && !detect.running && !sampleInvalid && !widthInvalid && !candidatesInvalid;

  const runDetection = () => {
    setCancelled(false);
    setStartedAtMs(Date.now());
    setNowMs(Date.now());
    void detect
      .run({
        reference,
        kind,
        database,
        schema,
        objectName,
        operation: "detectUniqueKey",
        sampleSize: sampleMode === "auto" ? null : sampleMode === "full" ? 0 : sampleSize,
        maxKeyColumns,
        maxCandidates,
        verifyCandidates: verify,
        trustDeclaredKeys,
      })
      .then((report) => {
        // The terminal success toast (DESIGN.md 8.2); a cancelled or failed run resolves null instead.
        if (report !== null) {
          toast.success(report.objectName !== null
            ? `Unique key detection finished for ${report.objectName}.`
            : "Unique key detection finished.");
        }
      })
      .finally(() => setHistoryVersion((v) => v + 1));
  };

  const cancelDetection = () => {
    detect.reset(); // aborts the long-poll and best-effort cancels the queued/running task server-side
    setCancelled(true);
    setHistoryVersion((v) => v + 1);
  };

  // ---- History ----------------------------------------------------------------------------------------------
  const historyColumns: Column<ComputeTaskSummary>[] = [
    {
      id: "target",
      header: "Object",
      render: (row) => (row.target !== null
        ? <Mono className="font-semibold">{row.target}</Mono>
        : <span className="text-[13px] text-muted-foreground">-</span>),
    },
    { id: "reference", header: "Datasource", render: (row) => <Mono>{row.sourceRef}</Mono> },
    { id: "status", header: "Status", render: (row) => <RunStatusBadge status={row.status} /> },
    { id: "enqueued", header: "Enqueued", render: (row) => <RelativeTime value={row.enqueuedUtc} /> },
    {
      id: "duration",
      header: "Duration",
      align: "right",
      render: (row) => <span className="font-mono tabular-nums">{durationLabel(row.startUtc, row.endUtc)}</span>,
    },
    {
      id: "requestedBy",
      header: "By",
      render: (row) => row.requestedBy ?? <span className="text-[13px] text-muted-foreground">-</span>,
    },
  ];

  if (!canOperate) {
    return (
      <Page data-testid="page-key-detection">
        <PageHeader title="Unique key detection" />
        <EmptyState
          icon={<Lock />}
          title="Operate scope required"
          description="Detection profiles the live source through a worker node, so it needs the operate scope. Ask an administrator for the operator role."
        />
      </Page>
    );
  }

  return (
    <Page data-testid="page-key-detection">
      <PageHeader
        title="Unique key detection"
        subtitle="Find the minimal column set(s) that uniquely identify a table's rows. A key the database already declares answers instantly from metadata; otherwise the rows are profiled on a worker node, on a random sample for large tables, and every reported key is verified against the whole table."
      />

      <Card className="gap-4 rounded-lg p-4">
        <h2 className="text-sm font-medium">Target</h2>
        <div className="flex flex-col gap-2 md:flex-row md:flex-wrap md:items-center">
          <ComboBoxField
            ariaLabel="Datasource"
            options={selectableSources}
            optionValue={(source) => source.reference}
            renderOption={(source) => (
              <>
                <Mono>{source.reference}</Mono>
                {source.kind !== null && <Badge variant="outline" className="ml-auto">{source.kind}</Badge>}
              </>
            )}
            value={reference === "" ? null : reference}
            onChange={(_value, source) => setScope({
              ref: source.reference, kind: source.kind, db: null, schema: null, object: null,
            })}
            clearOption={{
              label: "Clear selection",
              onClear: () => setScope({ ref: null, kind: null, db: null, schema: null, object: null }),
            }}
            loading={datasources.isPending}
            placeholder="pick a connection reference"
            loadingMessage="Loading the datasources..."
            emptyMessage={datasources.isError ? "Could not load the datasources." : "No detectable datasource matches."}
            testId="kd-datasource"
            className="w-full md:w-70"
          />
          <ComboBoxField
            ariaLabel="Database"
            options={databases.data?.databases.map((d) => d.name) ?? []}
            optionValue={(name) => name}
            renderOption={(name) => <span className="font-mono text-[12px]">{name}</span>}
            value={database}
            onChange={(value) => setScope({ db: value, schema: null, object: null })}
            clearOption={{
              label: "connection default",
              onClear: () => setScope({ db: null, schema: null, object: null }),
            }}
            loading={databases.running}
            disabled={!usable}
            placeholder="connection default"
            loadingMessage="Loading from the source..."
            testId="kd-database"
            className="w-full md:w-55"
          />
          <ComboBoxField
            ariaLabel="Schema"
            options={schemas.data?.schemas.map((s) => s.name) ?? []}
            optionValue={(name) => name}
            renderOption={(name) => <span className="font-mono text-[12px]">{name}</span>}
            value={schema}
            onChange={(value) => setScope({ schema: value, object: null })}
            clearOption={{
              label: "all schemas",
              onClear: () => setScope({ schema: null, object: null }),
            }}
            loading={schemas.running}
            disabled={!usable}
            placeholder="all schemas"
            loadingMessage="Loading from the source..."
            testId="kd-schema"
            className="w-full md:w-50"
          />
          <ComboBoxField
            ariaLabel="Table or view"
            options={objects.data?.items ?? []}
            optionValue={(option) => `${option.schema}.${option.name}`}
            renderOption={(option) => (
              <>
                <Mono>{option.schema}.{option.name}</Mono>
                <Badge variant="outline" className="ml-auto">{option.type}</Badge>
              </>
            )}
            value={selectedObject !== null ? `${selectedObject.schema}.${selectedObject.name}` : null}
            onChange={(_value, option) => setScope({ schema: option.schema, object: option.name })}
            clearOption={{
              label: "Clear selection",
              onClear: () => setScope({ schema: null, object: null }),
            }}
            onSearchChange={setObjectInput}
            loading={objects.running}
            disabled={!usable}
            placeholder="type to search"
            loadingMessage="Searching on a worker node..."
            emptyMessage="No tables or views match."
            testId="kd-object"
            className="w-full min-w-60 flex-1 md:w-auto"
          />
        </div>
        {databases.error !== null && (
          <Alert variant="destructive" data-testid="kd-databases-error">
            <CircleAlert />
            <AlertDescription>{databases.error}</AlertDescription>
          </Alert>
        )}
        {schemas.error !== null && databases.error === null && (
          <Alert variant="destructive" data-testid="kd-schemas-error">
            <CircleAlert />
            <AlertDescription>{schemas.error}</AlertDescription>
          </Alert>
        )}
        {objects.error !== null && (
          <Alert variant="destructive" data-testid="kd-objects-error">
            <CircleAlert />
            <AlertDescription>{objects.error}</AlertDescription>
          </Alert>
        )}

        <h2 className="text-sm font-medium">Options</h2>
        <div className="flex flex-col gap-3 lg:flex-row lg:flex-wrap lg:items-center lg:gap-4">
          <RadioGroup
            value={sampleMode}
            onValueChange={(value) => setSampleMode(value as "auto" | "full" | "custom")}
            className="flex flex-row flex-wrap items-center gap-4"
            data-testid="kd-sample-mode"
          >
            <Tooltip>
              <TooltipTrigger asChild>
                <Label htmlFor="kd-sample-mode-auto" className="flex items-center gap-2 text-[13px] font-normal">
                  <RadioGroupItem id="kd-sample-mode-auto" value="auto" />
                  Auto sample
                </Label>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                Tables over 2 million rows profile a 500,000-row random sample; smaller tables get a full scan.
              </TooltipContent>
            </Tooltip>
            <Tooltip>
              <TooltipTrigger asChild>
                <Label htmlFor="kd-sample-mode-full" className="flex items-center gap-2 text-[13px] font-normal">
                  <RadioGroupItem id="kd-sample-mode-full" value="full" />
                  Full scan
                </Label>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                Profile every row. Exact, but expensive on a large table.
              </TooltipContent>
            </Tooltip>
            <Tooltip>
              <TooltipTrigger asChild>
                <Label htmlFor="kd-sample-mode-custom" className="flex items-center gap-2 text-[13px] font-normal">
                  <RadioGroupItem id="kd-sample-mode-custom" value="custom" />
                  Sample
                </Label>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                Profile a random sample of this many rows; reported keys are still verified against the whole table.
              </TooltipContent>
            </Tooltip>
          </RadioGroup>
          {sampleMode === "custom" && (
            <div className="flex flex-col gap-1">
              <Label htmlFor="kd-sample-size" className="text-xs font-normal text-muted-foreground">
                Sample rows
              </Label>
              <Input
                id="kd-sample-size"
                type="number"
                min={1}
                value={Number.isFinite(sampleSize) ? sampleSize : ""}
                onChange={(e) => setSampleSize(Number.parseInt(e.target.value, 10))}
                aria-invalid={sampleInvalid}
                className="h-8 w-40"
                data-testid="kd-sample-size"
              />
              {sampleInvalid && <span className="text-xs text-destructive">At least 1 row.</span>}
            </div>
          )}
          <div className="flex flex-col gap-1">
            <Label htmlFor="kd-max-columns" className="text-xs font-normal text-muted-foreground">
              Max key width
            </Label>
            <Input
              id="kd-max-columns"
              type="number"
              min={1}
              max={16}
              value={Number.isFinite(maxKeyColumns) ? maxKeyColumns : ""}
              onChange={(e) => setMaxKeyColumns(Number.parseInt(e.target.value, 10))}
              aria-invalid={widthInvalid}
              className="h-8 w-36"
              data-testid="kd-max-columns"
            />
            {widthInvalid && <span className="text-xs text-destructive">1 to 16 columns.</span>}
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="kd-max-candidates" className="text-xs font-normal text-muted-foreground">
              Max candidates
            </Label>
            <Input
              id="kd-max-candidates"
              type="number"
              min={1}
              max={50}
              value={Number.isFinite(maxCandidates) ? maxCandidates : ""}
              onChange={(e) => setMaxCandidates(Number.parseInt(e.target.value, 10))}
              aria-invalid={candidatesInvalid}
              className="h-8 w-36"
              data-testid="kd-max-candidates"
            />
            {candidatesInvalid && <span className="text-xs text-destructive">1 to 50 candidates.</span>}
          </div>
          <Tooltip>
            <TooltipTrigger asChild>
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch checked={verify} onCheckedChange={setVerify} data-testid="kd-verify" />
                Verify on full table
              </Label>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs">
              Confirm every sampled candidate against the whole table before reporting it as unique.
            </TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch
                  checked={trustDeclaredKeys}
                  onCheckedChange={setTrustDeclaredKeys}
                  data-testid="kd-trust-declared"
                />
                Use declared keys
              </Label>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs">
              When the database already enforces a unique index or constraint, answer from that metadata without
              reading a row. Turn off to profile the data regardless.
            </TooltipContent>
          </Tooltip>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <Button size="sm" disabled={!canRun} onClick={runDetection} data-testid="kd-run">
            {detect.running ? <Loader2 className="animate-spin" /> : <KeyRound />}
            {detect.running ? "Profiling..." : "Detect unique key"}
          </Button>
          {detect.running && (
            <>
              <Button variant="ghost" size="sm" onClick={cancelDetection} data-testid="kd-cancel">
                <Ban />
                Cancel
              </Button>
              {startedAtMs !== null && (
                <span className="text-[13px] text-muted-foreground" data-testid="kd-elapsed">
                  {elapsedLabel(startedAtMs, nowMs)} elapsed; a large table can take a while. Leaving this page
                  cancels the detection.
                </span>
              )}
            </>
          )}
        </div>
      </Card>

      {detect.error !== null && (
        <Alert variant="destructive" data-testid="kd-error">
          <CircleAlert />
          <AlertDescription>{detect.error}</AlertDescription>
        </Alert>
      )}
      {cancelled && detect.data === null && detect.error === null && (
        <Alert className="border-info/50 text-info" data-testid="kd-cancelled">
          <Info />
          <AlertDescription className="text-info/90">The detection was cancelled.</AlertDescription>
        </Alert>
      )}

      {detect.data !== null && (
        <Card className="gap-3 rounded-lg p-4">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-sm font-medium">Result</h2>
            {detect.data.objectName !== null && <Mono className="font-semibold">{detect.data.objectName}</Mono>}
          </div>
          <UniqueKeyReportView report={detect.data} data-testid="kd-result" />
        </Card>
      )}

      <div className="flex flex-col gap-2">
        <h2 className="text-sm font-medium">
          Detection history{reference !== "" ? <> for <Mono>{reference}</Mono></> : null}
        </h2>
        <PagedTable
          queryKey={["compute-tasks", "detectUniqueKey", reference, historyVersion]}
          fetchPage={(page, pageSize) => datasourceApi.tasks({
            operation: "detectUniqueKey",
            reference: reference === "" ? undefined : reference,
            page,
            pageSize,
          })}
          columns={historyColumns}
          rowKey={(row) => row.taskId}
          onRowClick={(row) => setOpenTaskId(row.taskId)}
          pollMs={10_000}
          emptyMessage="No detections have run yet. Pick a table above and run one."
          data-testid="kd-history"
        />
      </div>

      {openTaskId !== null && <HistoryTaskSheet taskId={openTaskId} onClose={() => setOpenTaskId(null)} />}
    </Page>
  );
}
