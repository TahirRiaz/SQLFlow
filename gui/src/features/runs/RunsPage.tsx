import { useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Play } from "lucide-react";
import { readLocalStorageState, useLocalStorageState, useUrlSeed } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import type { RunStatus, RunSummary } from "../../api/types";
import { pipelineApi, runApi, scheduleApi } from "../../api/endpoints";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { FilterCombobox, type FilterOption } from "../../components/FilterCombobox";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column, type TableGrouping } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge, rollupStatus } from "../../components/StatusBadge";
import { formatDurationSeconds } from "../../lib/time";
import { TriggerRunDialog } from "./TriggerRunDialog";

const statuses: RunStatus[] = ["queued", "running", "succeeded", "failed", "cancelled", "skipped"];
const kinds = ["all", "file", "ing", "api", "cpy", "sftp", "exp", "trl", "sp", "inv", "hc", "scm", "batch", "cal"];

/** A right-aligned numeric cell: the value with thousands separators, or "-" when it is null/zero (a flow that
 * touched no rows, or a non-file flow with no file count). */
function numCell(value: number | null | undefined) {
  return <span className="font-mono tabular-nums">{value ? value.toLocaleString() : "-"}</span>;
}

// The identity columns lead every layout; the data-impact columns (files read, rows loaded / inserted / updated)
// trail it. Pool and commit are omitted here on purpose: they are per-run plumbing details that live on the run
// detail page, not signal an operator scans a run board for.
const statusColumn: Column<RunSummary> = {
  id: "status",
  header: "Status",
  render: (row) => <RunStatusBadge status={row.status} />,
};
const flowColumn: Column<RunSummary> = {
  id: "flow",
  header: "Flow",
  render: (row) => <span className="font-mono text-[12px] font-medium">{row.flowName}</span>,
};
const kindColumn: Column<RunSummary> = { id: "kind", header: "Kind", render: (row) => row.flowKind };
const enqueuedColumn: Column<RunSummary> = {
  id: "enqueued",
  header: "Enqueued",
  render: (row) => <RelativeTime value={row.enqueuedUtc ?? row.writtenUtc} />,
};
const durationColumn: Column<RunSummary> = {
  id: "duration",
  header: "Duration",
  render: (row) => (row.durationSeconds != null ? formatDurationSeconds(row.durationSeconds) : "-"),
};
const impactColumns: Column<RunSummary>[] = [
  { id: "files", header: "Files", align: "right", render: (row) => numCell(row.fileCount) },
  { id: "loaded", header: "Loaded", align: "right", render: (row) => numCell(row.rowsLoaded) },
  { id: "inserted", header: "Inserted", align: "right", render: (row) => numCell(row.rowsInserted) },
  { id: "updated", header: "Updated", align: "right", render: (row) => numCell(row.rowsUpdated) },
];

const baseColumns: Column<RunSummary>[] = [
  statusColumn,
  flowColumn,
  kindColumn,
  enqueuedColumn,
  durationColumn,
  ...impactColumns,
];

// In the flat (ungrouped) view batch and step become ordinary columns; grouped, they live in the header rows.
const flatColumns: Column<RunSummary>[] = [
  statusColumn,
  flowColumn,
  kindColumn,
  { id: "batch", header: "Batch", render: (row) => row.batch },
  {
    id: "step",
    header: "Step",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.wave >= 0 ? row.wave : "-"}</span>,
  },
  enqueuedColumn,
  durationColumn,
  ...impactColumns,
];

/** Aggregates one group's rows for its header line: when it started, total duration, how many failed, and the
 * group's combined data impact (files read and rows loaded / inserted / updated) so a collapsed schedule or batch
 * shows how much data its last run moved without being expanded. */
function groupStats(rows: RunSummary[]) {
  let durationTotal = 0;
  let hasDuration = false;
  let earliest: string | null = null;
  let failed = 0;
  let files = 0;
  let loaded = 0;
  let inserted = 0;
  let updated = 0;
  for (const row of rows) {
    if (row.durationSeconds != null) {
      durationTotal += row.durationSeconds;
      hasDuration = true;
    }

    const at = row.enqueuedUtc ?? row.writtenUtc;
    if (earliest === null || new Date(at).getTime() < new Date(earliest).getTime()) {
      earliest = at;
    }

    if (row.status === "failed") {
      failed += 1;
    }

    files += row.fileCount;
    loaded += row.rowsLoaded ?? 0;
    inserted += row.rowsInserted ?? 0;
    updated += row.rowsUpdated ?? 0;
  }

  return { durationTotal: hasDuration ? durationTotal : null, earliest, failed, files, loaded, inserted, updated };
}

function GroupStatsInline({ rows }: { rows: RunSummary[] }) {
  const stats = groupStats(rows);
  const impact: { label: string; value: number }[] = [
    { label: "files", value: stats.files },
    { label: "loaded", value: stats.loaded },
    { label: "inserted", value: stats.inserted },
    { label: "updated", value: stats.updated },
  ];
  return (
    <>
      {/* The group's headline status: worst-wins across its runs, so a collapsed group reads green only when
          everything under it succeeded. This is the at-a-glance indicator on every level's header. */}
      <RunStatusBadge status={rollupStatus(rows.map((row) => row.status))} testId="group-rollup-status" />
      <span className="text-[13px] text-muted-foreground">
        started <RelativeTime value={stats.earliest} />
      </span>
      {stats.durationTotal != null && (
        <span className="text-[13px] text-muted-foreground">
          duration {formatDurationSeconds(stats.durationTotal)}
        </span>
      )}
      <span className="text-[13px] text-muted-foreground">({rows.length})</span>
      {/* Only non-zero impact metrics show, so a group that loaded nothing stays uncluttered while one that moved
          data reports its totals inline (e.g. "120 files", "45,678 loaded"). */}
      {impact.filter((metric) => metric.value > 0).map((metric) => (
        <span key={metric.label} className="text-[13px] tabular-nums text-muted-foreground">
          {metric.value.toLocaleString()} {metric.label}
        </span>
      ))}
      {stats.failed > 0 && (
        <span className="text-[13px] font-medium text-destructive">{stats.failed} failed</span>
      )}
    </>
  );
}

/** The bucket a run whose pipeline joined no schedule reports under, sorted last after every named schedule. */
const UNSCHEDULED = "Unscheduled";

/** The schedule name each pipeline belongs to, keyed by pipeline id: a run inherits it through its pipeline. */
type ScheduleByPipeline = Map<string, string>;

const compare = (a: number | string, b: number | string) => (a < b ? -1 : a > b ? 1 : 0);

// The batch-report layout carried over from classic SQLFlow, now anchored on the SCHEDULE that runs the source:
// each pipeline's LAST run clusters under its schedule, then under its batch, then under the lineage step, so the
// grouped view answers "for this source's cadence, what failed and what needs fixing" at a glance. Within a
// schedule the batches read in cascade (dependency) order, by the earliest wave any of a batch's flows runs at,
// so a source's copy -> detail flow reads top to bottom and a stray legacy batch cannot wedge itself between two
// live ones by an alphabetical accident. Full run history lives in the flat view and on the pipeline detail page.
// The "Run batch" action launches the whole batch (every flow in it, in dependency order) as one run group.
function makeScheduleGrouping(
  scheduleByPipeline: ScheduleByPipeline,
  onRunBatch: (repoId: string | null, batch: string) => void,
): TableGrouping<RunSummary> {
  const scheduleOf = (row: RunSummary) => scheduleByPipeline.get(row.pipelineId) ?? UNSCHEDULED;
  // A schedule+batch identity for the cascade-rank map; the NUL delimiter cannot occur in a name, so no pair of
  // distinct (schedule, batch) values collides on it.
  const cascadeKey = (row: RunSummary) => `${scheduleOf(row)}\u0000${row.batch}`;

  return {
    // Sort the page into schedule -> batch(cascade) -> step(wave) -> flow order so the contiguous clustering
    // below reconstructs exactly that tree. The batch's cascade rank is the earliest wave any of its flows runs
    // at (a wave of -1, "lineage not computed", sorts last); named schedules order by name, the Unscheduled
    // bucket last.
    transform: (rows) => {
      const cascadeRank = new Map<string, number>();
      for (const row of rows) {
        const wave = row.wave >= 0 ? row.wave : Number.MAX_SAFE_INTEGER;
        const current = cascadeRank.get(cascadeKey(row));
        if (current === undefined || wave < current) {
          cascadeRank.set(cascadeKey(row), wave);
        }
      }

      const scheduleRank = (row: RunSummary) => {
        const name = scheduleOf(row);
        return name === UNSCHEDULED ? "\uffff" : name.toLowerCase();
      };

      return [...rows].sort((a, b) =>
        compare(scheduleRank(a), scheduleRank(b))
        || compare(cascadeRank.get(cascadeKey(a))!, cascadeRank.get(cascadeKey(b))!)
        || compare(a.batch, b.batch)
        || compare(a.wave, b.wave)
        || compare(a.flowName, b.flowName));
    },
    // Every level starts collapsed: the board opens as a list of schedules with their rollup (count, failures),
    // and an operator drills in a level at a time (schedule -> batch -> step -> runs), opening only the node they
    // care about rather than having one click cascade a whole source open.
    levels: [
      {
        key: scheduleOf,
        defaultCollapsed: true,
        renderHeader: (rows) => (
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1" data-testid="schedule-group-header">
            <span className="text-[13px] font-semibold">
              {scheduleOf(rows[0]) === UNSCHEDULED ? UNSCHEDULED : `Schedule: ${scheduleOf(rows[0])}`}
            </span>
            <GroupStatsInline rows={rows} />
          </div>
        ),
      },
      {
        key: (row) => row.batch,
        defaultCollapsed: true,
        renderHeader: (rows) => (
          // grow makes this row fill the header (it is a content-sized flex item inside the node row's cell), so
          // the button's ml-auto really pushes it to the right edge instead of leaving it mid-row where it would
          // swallow clicks meant to collapse the group.
          <div className="flex grow flex-wrap items-baseline gap-x-3 gap-y-1" data-testid="batch-group-header">
            <span className="text-[13px] font-medium">Batch: {rows[0].batch}</span>
            <GroupStatsInline rows={rows} />
            <Button
              variant="outline"
              size="xs"
              className="ml-auto"
              onClick={(e) => {
                e.stopPropagation();
                onRunBatch(rows[0].repoId, rows[0].batch);
              }}
              data-testid="run-batch"
            >
              <Play />
              Run batch
            </Button>
          </div>
        ),
      },
      {
        key: (row) => String(row.wave),
        defaultCollapsed: true,
        renderHeader: (rows) => (
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1" data-testid="step-group-header">
            <span className="text-[13px] font-medium">Step: {rows[0].wave >= 0 ? rows[0].wave : "?"}</span>
            <GroupStatsInline rows={rows} />
          </div>
        ),
      },
    ],
  };
}

/** The run inbox: live-polled list with status/flow/schedule/batch/kind filters and the entry point for
 * triggering runs. It defaults to the "Last run" scope grouped by schedule: each flow's newest run under the
 * schedule that runs the source, then its batch (in cascade order) and lineage step, every level collapsed so an
 * operator drills into one source at a time to see what its last execution did. The "All" scope opens the full
 * run history; the group switch flattens the tree to the plain inbox. */
export default function RunsPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [triggerOpen, setTriggerOpen] = useState(false);
  const [batchRun, setBatchRun] = useState<{ repoId: string | null; batch: string } | null>(null);
  const [status, setStatus] = useLocalStorageState<RunStatus | null>("sqlflow.filters.runs.status", null);
  const [kind, setKind] = useLocalStorageState("sqlflow.filters.runs.kind", "all");
  const [flowNameInput, setFlowNameInput] = useLocalStorageState("sqlflow.filters.runs.flowName", "");
  // Seed the debounced value from the same remembered text so the first query runs filtered, with no flash.
  const [flowName, setFlowName] = useState(() =>
    readLocalStorageState("sqlflow.filters.runs.flowName", "").trim());
  const [batch, setBatch] = useLocalStorageState("sqlflow.filters.runs.batch", "");
  // The schedule name on the Schedules board deep-links here as /runs?scheduleId=<id>, so a click lands on that
  // schedule's runs with the filter already applied. This is a transient deep-link context, not remembered.
  const [scheduleId, setScheduleId] = useState(() => searchParams.get("scheduleId") ?? "");
  const [grouped, setGrouped] = useLocalStorageState("sqlflow.filters.runs.grouped", true);
  // "last" (the default) shows each flow's newest run: the outcome of the most recent execution, "what happened
  // last" per schedule. "all" opens the full run history. Independent of grouping, which is only the tree shape.
  const [view, setView] = useLocalStorageState<"last" | "all">("sqlflow.filters.runs.view", "last");

  // The status bar's workload segments deep-link here as /runs?status=running|queued, overriding the remembered
  // filter (ignoring an unrecognised value).
  useUrlSeed(searchParams.get("status"), (value) => {
    if (statuses.includes(value as RunStatus)) setStatus(value as RunStatus);
  });

  useEffect(() => {
    const handle = window.setTimeout(() => setFlowName(flowNameInput.trim()), 400);
    return () => window.clearTimeout(handle);
  }, [flowNameInput]);

  // Schedules and batches are the estate's source list, not its flow list, so both are small, change rarely, and
  // load once for the whole board: they populate the schedule/batch filter dropdowns AND the grouping's
  // run-to-schedule map. A big page covers every repo in one request.
  const schedulesQuery = useQuery({
    queryKey: ["schedules", "for-runs-board"],
    queryFn: () => scheduleApi.list({ pageSize: 500 }),
    staleTime: 60_000,
  });

  const batchesQuery = useQuery({
    queryKey: ["pipelines", "batches", "for-runs-board"],
    queryFn: () => pipelineApi.batches(),
    staleTime: 60_000,
  });

  // The schedule dropdown's options: one per schedule, valued by id (so two like-named schedules in different
  // repos stay distinct), hinted with how many flows a fire runs. Ordered by name, as the server returns them.
  const scheduleOptions = useMemo<FilterOption[]>(
    () => (schedulesQuery.data?.items ?? []).map((schedule) => ({
      value: schedule.id,
      label: schedule.name,
      hint: `${schedule.memberPipelineIds.length} ${schedule.memberPipelineIds.length === 1 ? "flow" : "flows"}`,
    })),
    [schedulesQuery.data],
  );

  // The batch dropdown's options: the estate's distinct batch labels (the same label in several repos is one
  // option, since the runs filter matches on the label), hinted with the total flow count, sorted by label.
  const batchOptions = useMemo<FilterOption[]>(() => {
    const flowsByBatch = new Map<string, number>();
    for (const row of batchesQuery.data ?? []) {
      flowsByBatch.set(row.batch, (flowsByBatch.get(row.batch) ?? 0) + row.flowCount);
    }

    return [...flowsByBatch.entries()]
      .sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0))
      .map(([label, flows]) => ({ value: label, label, hint: `${flows} ${flows === 1 ? "flow" : "flows"}` }));
  }, [batchesQuery.data]);

  const scheduleByPipeline = useMemo<ScheduleByPipeline>(() => {
    const map: ScheduleByPipeline = new Map();
    for (const schedule of schedulesQuery.data?.items ?? []) {
      for (const pipelineId of schedule.memberPipelineIds) {
        if (!map.has(pipelineId)) {
          map.set(pipelineId, schedule.name);
        }
      }
    }

    return map;
  }, [schedulesQuery.data]);

  const grouping = useMemo(
    () => makeScheduleGrouping(scheduleByPipeline, (repoId, batchName) => setBatchRun({ repoId, batch: batchName })),
    [scheduleByPipeline],
  );

  return (
    <Page data-testid="page-runs">
      <PageHeader
        title="Runs"
        actions={(
          <Button size="sm" onClick={() => setTriggerOpen(true)} data-testid="open-trigger-run">
            <Play />
            Trigger run
          </Button>
        )}
      />

      <FilterBar>
        {/* Free-text search leads the row (DESIGN.md 7.1), then the filters read left to right down the source
            hierarchy (schedule -> batch -> kind), through the run's state and history scope (status, last/all), to
            the display toggle (group). */}
        <Input
          value={flowNameInput}
          onChange={(e) => setFlowNameInput(e.target.value)}
          placeholder="Flow name"
          aria-label="Flow name"
          data-testid="filter-flow-name"
          className={cn("h-8 w-44", flowNameInput !== "" && activeFilterClass)}
        />
        <FilterCombobox
          options={scheduleOptions}
          value={scheduleId}
          onChange={setScheduleId}
          placeholder="Schedule"
          searchPlaceholder="Search schedules"
          emptyText="No schedules found."
          ariaLabel="Filter by schedule"
          testId="filter-schedule"
          className="w-48"
        />
        <FilterCombobox
          options={batchOptions}
          value={batch}
          onChange={setBatch}
          placeholder="Batch"
          searchPlaceholder="Search batches"
          emptyText="No batches found."
          ariaLabel="Filter by batch"
          testId="filter-batch"
          className="w-40"
        />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" active={kind !== "all"} className="h-8 w-28" aria-label="Kind" data-testid="filter-kind">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((k) => (
              <SelectItem key={k} value={k}>{k}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={status ?? "all"}
          onValueChange={(value) => setStatus(value === "all" ? null : (value as RunStatus))}
        >
          <SelectTrigger size="sm" active={status !== null} className="h-8 w-32" aria-label="Status" data-testid="filter-status">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">all statuses</SelectItem>
            {statuses.map((s) => (
              <SelectItem key={s} value={s}>{s}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={view}
          onValueChange={(value) => value && setView(value as "last" | "all")}
          aria-label="Run history scope"
        >
          <ToggleGroupItem value="last" data-testid="filter-view-last" className="h-8 px-2.5 text-xs">
            Last
          </ToggleGroupItem>
          <ToggleGroupItem value="all" data-testid="filter-view-all" className="h-8 px-2.5 text-xs">
            All
          </ToggleGroupItem>
        </ToggleGroup>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch
            checked={grouped}
            onCheckedChange={setGrouped}
            data-testid="group-by-batch"
          />
          Group by schedule
        </Label>
      </FilterBar>

      <PagedTable
        queryKey={["runs", "list", status, flowName, kind, batch, scheduleId, view, grouped]}
        fetchPage={(page, pageSize) =>
          runApi.list({
            status: status ?? undefined,
            flowName: flowName === "" ? undefined : flowName,
            flowKind: kind === "all" ? undefined : kind,
            batch: batch === "" ? undefined : batch,
            scheduleId: scheduleId === "" ? undefined : scheduleId,
            latest: view === "last" || undefined,
            page,
            pageSize,
          })}
        columns={grouped ? baseColumns : flatColumns}
        rowKey={(row) => row.runId}
        onRowClick={(row) => navigate(`/runs/${row.runId}`)}
        pollMs={5000}
        emptyMessage="No runs match the current filters."
        grouping={grouped ? grouping : undefined}
        data-testid="runs-table"
      />

      <TriggerRunDialog open={triggerOpen} onClose={() => setTriggerOpen(false)} />
      <TriggerRunDialog
        open={batchRun !== null}
        onClose={() => setBatchRun(null)}
        repoId={batchRun?.repoId ?? undefined}
        batch={batchRun?.batch}
        scope="batch"
      />
    </Page>
  );
}
