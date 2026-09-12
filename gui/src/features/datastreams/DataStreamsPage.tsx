import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Activity, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { DataStream, StreamStatus } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column, type TableGrouping } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { pollingInterval } from "../../hooks/usePolling";
import { StreamDetailSheet } from "./StreamDetailSheet";
import { StreamStatusBadge } from "./StreamStatusBadge";
import {
  confidenceLabel, cycleHint, formatDays, formatRows, howOftenItLoads, stageLabels, stageMeaning, statusLabels,
  statusRank, whatIsWrong,
} from "./streamPresentation";

/** The last two parts of a qualified name (schema.table), which is what distinguishes pre.X from arc.X
 * without spending a column on the database. */
function shortTarget(qualified: string | null): string | null {
  if (qualified === null) {
    return null;
  }

  const parts = qualified.split(".");
  return parts.length <= 2 ? qualified : parts.slice(-2).join(".");
}

const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(148px,1fr))] gap-3";

const windowChoices = [
  { days: 30, label: "30d" },
  { days: 60, label: "60d" },
  { days: 90, label: "90d" },
];

const groupChoices = [
  { value: "schedule", label: "By schedule" },
  { value: "source", label: "By source" },
  { value: "none", label: "Flat" },
];

/**
 * One collapsed group: the source (or schedule), its worst verdict, and what it is made of. A source with
 * fifty objects is one line here, and the summary is meant to answer "do I need to open this" without
 * opening it.
 */
function GroupHeader({ label, caption, rows }: { label: string; caption?: string; rows: DataStream[] }) {
  // Rows arrive ranked most urgent first and the grouping preserves that, so the first row IS the worst.
  const worst = rows[0];
  const counts = new Map<StreamStatus, number>();
  for (const row of rows) {
    counts.set(row.status, (counts.get(row.status) ?? 0) + 1);
  }

  const breakdown = [...counts.entries()]
    .sort((a, b) => statusRank(a[0]) - statusRank(b[0]))
    .map(([status, n]) => `${n} ${statusLabels[status].toLowerCase()}`)
    .join(" · ");

  return (
    <div className="flex min-w-0 flex-1 items-center gap-3">
      <span className="shrink-0 text-[13px] font-medium">{label}</span>
      {caption !== undefined && (
        <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{caption}</span>
      )}
      <StreamStatusBadge status={worst.status} severity={worst.severity} />
      <span className="truncate text-[11px] text-muted-foreground">
        {rows.length} table{rows.length === 1 ? "" : "s"} · {breakdown}
      </span>
    </div>
  );
}

/** The filter chips carry the same words as the verdicts themselves, so a reader never has to map one
 * vocabulary onto another. */
const statusFilters = [
  { value: "", label: "All" },
  { value: "stalled", label: "Stopped" },
  { value: "degraded", label: "Missing data" },
  { value: "watch", label: "Worth a look" },
  { value: "healthy", label: "OK" },
];

/**
 * DataStream anomaly detection: which tables have stopped receiving data.
 *
 * The board is split by WHO a finding belongs to, because that is the first thing anyone needs to know and
 * mixing the two sides makes both unreadable. A vendor that did not deliver and a transformation of ours that
 * broke are different incidents with different owners, and one quiet upstream would otherwise light up its
 * whole downstream chain as a dozen separate findings that are all the same finding. Vendor deliveries are the
 * default view; our own processing is one click away, and so is everything at once.
 *
 * The stage column sharpens the same question. A source-side stream failing at "Vendor fetch" means nothing
 * arrived from them; the same stream failing at "File ingestion" or "Load to archive" means it arrived and we
 * did not take it in.
 *
 * Every column answers a question in the words an operator would use, because the algorithm's vocabulary
 * ("degraded", "gap-days", "2 of 6 detectors") is precise and useless at a glance. What is wrong, how often
 * this table normally loads, when data last arrived, how many days it missed against how many it usually
 * misses, and how much to trust the finding. The statistics behind each of those live one click away in the
 * detail sheet, where there is room to show the reasoning rather than just the verdict.
 *
 * Two switches change what the numbers MEAN rather than just what is shown, which is why they sit on the page
 * rather than in a menu. "Include backfills" puts operator-driven reprocessing back into the baseline, and a
 * history replay will then make every ordinary day after it look like a collapse. "Include unscheduled" adds
 * the flows nothing schedules, which have no say in whether data is delivered: measuring them against a
 * delivery cadence manufactures findings about a promise nobody made.
 *
 * Grouping is by SCHEDULE by default, because a schedule is what actually fires together: one fire runs a
 * source's whole wave, so a group missing the same six days is one incident and not nine.
 */
export default function DataStreamsPage() {
  const [days, setDays] = useLocalStorageState<number>("datastreams.windowDays", 60);
  const [status, setStatus] = useLocalStorageState<string>("datastreams.status", "");
  const [scope, setScope] = useLocalStorageState<string>("datastreams.scope", "source");
  const [groupBy, setGroupBy] = useLocalStorageState<string>("datastreams.groupBy", "schedule");
  const [includeUnscheduled, setIncludeUnscheduled] =
    useLocalStorageState<boolean>("datastreams.includeUnscheduled", false);
  const [includeBackfills, setIncludeBackfills] = useLocalStorageState<boolean>("datastreams.includeBackfills", false);
  const [drill, setDrill] = useState<{ pipelineId: string; flowName: string } | null>(null);
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: ["datastreams", days, status, scope, includeBackfills, includeUnscheduled],
    queryFn: () => dataStreamApi.list({
      days,
      status: status === "" ? undefined : status,
      scope,
      includeBackfills,
      includeUnscheduled,
      limit: 300,
    }),
    refetchInterval: pollingInterval(120_000),
  });

  /**
   * Re-run the analysis against the catalog as it stands right now. Every verdict on this page is computed
   * from run history on each request, so a load that landed since the last poll is invisible until something
   * asks again, and the two-minute poll is far too slow for the "I just ran it, did it help" loop. Invalidating
   * the whole "datastreams" key deliberately takes the open detail sheet with it: a board that says one thing
   * and a sheet that says another is worse than either alone.
   */
  const recheck = () => {
    void queryClient.invalidateQueries({ queryKey: ["datastreams"] });
  };

  const columns = useMemo<Column<DataStream>[]>(() => [
    {
      id: "table",
      header: "Table",
      width: 300,
      render: (s) => (
        <div className="flex min-w-0 flex-col">
          <span className="truncate font-mono text-xs" title={s.flowName}>{s.flowName}</span>
          <span className="truncate text-[11px] text-muted-foreground" title={s.targetObject ?? undefined}>
            {/* schema.table, because the bare name does not distinguish a staging copy from the archive one
                it feeds; the full three-part name is on hover. */}
            {shortTarget(s.targetObject) ?? s.batch ?? "unknown target"}
          </span>
        </div>
      ),
    },
    {
      id: "stage",
      header: "Stage",
      width: 140,
      render: (s) => (
        <span className="text-[13px]" title={stageMeaning[s.stage]}>{stageLabels[s.stage]}</span>
      ),
    },
    {
      id: "wrong",
      header: "What's wrong",
      width: 190,
      render: (s) => (
        <div className="flex min-w-0 flex-col gap-0.5">
          <StreamStatusBadge status={s.status} severity={s.severity} />
          <span className="truncate text-[11px] text-muted-foreground" title={s.summary}>{whatIsWrong(s)}</span>
        </div>
      ),
    },
    {
      id: "cadence",
      header: "Normally loads",
      width: 165,
      render: (s) => (
        <div className="flex min-w-0 flex-col">
          <span className="truncate text-[13px]">
            {howOftenItLoads(s.profile.pattern, s.profile.expectedGapDays)}
          </span>
          <span className="truncate font-mono text-[11px] tabular-nums text-muted-foreground">
            {s.profile.pattern.typicalRows > 0
              ? `~${formatRows(s.profile.pattern.typicalRows, true)} rows each time`
              : "no typical size yet"}
          </span>
          {/* The recurring delivery the rhythm alone cannot express. Without it a row reads "every day,
              ~9k rows" for a vendor that also ships double that every fortnight, and every fortnight the
              board would have to explain itself. */}
          {cycleHint(s.profile.pattern) !== null && (
            <span className="truncate text-[11px] text-muted-foreground" title={s.profile.pattern.cycle?.description}>
              + {cycleHint(s.profile.pattern)}
            </span>
          )}
        </div>
      ),
    },
    {
      id: "lastLoad",
      header: "Data last arrived",
      width: 130,
      align: "right",
      render: (s) => (
        <span className="font-mono text-xs tabular-nums">{formatDays(s.profile.daysSinceLastLoad)}</span>
      ),
    },
    {
      id: "missed",
      header: "Days with no rows",
      width: 130,
      align: "right",
      render: (s) => {
        const missed = s.profile.unexpectedNullDays;
        const usual = s.profile.predictedNullDays;
        return (
          <div className="flex flex-col items-end">
            <span className={`font-mono text-xs tabular-nums ${missed > usual ? "text-destructive" : ""}`}>
              {missed}
            </span>
            <span className="text-[11px] text-muted-foreground">
              {s.profile.noRunDays > 0
                ? `${s.profile.noRunDays} did not run`
                : usual < 0.5 ? "all ran, none loaded" : `usually ${usual.toFixed(0)}`}
            </span>
          </div>
        );
      },
    },
    {
      id: "confidence",
      header: "Confidence",
      width: 110,
      align: "right",
      render: (s) => {
        const confidence = confidenceLabel(s);
        return confidence === null
          ? <span className="text-xs text-muted-foreground">-</span>
          : <span className="text-xs" title={confidence.title}>{confidence.label}</span>;
      },
    },
  ], []);

  // Grouping keys off STRUCTURE, never the flow's name: the source comes from the repository layout (or
  // schedule membership), and the schedule from what actually fires together. A misnamed flow still lands
  // with its siblings.
  const grouping = useMemo<TableGrouping<DataStream> | undefined>(() => {
    if (groupBy === "none") {
      return undefined;
    }

    const keyOf = groupBy === "schedule"
      ? (row: DataStream) => row.scheduleName ?? "On no schedule"
      : (row: DataStream) => row.source;

    return {
      // The table clusters CONTIGUOUS rows, so the rows have to arrive already grouped. Sorting by each
      // key's first appearance keeps the server's most-urgent-first ranking in two ways at once: the group
      // holding the worst stream comes first, and within a group the order is untouched (the sort is stable).
      transform: (rows) => {
        const firstSeen = new Map<string, number>();
        rows.forEach((row, index) => {
          const key = keyOf(row);
          if (!firstSeen.has(key)) {
            firstSeen.set(key, index);
          }
        });
        return [...rows].sort((a, b) => firstSeen.get(keyOf(a))! - firstSeen.get(keyOf(b))!);
      },
      levels: [{
        key: keyOf,
        // Collapsed, because the whole point is an overview: fifty objects of one source are one line until
        // someone asks for them.
        defaultCollapsed: true,
        renderHeader: (rows) => (
          <GroupHeader
            label={keyOf(rows[0])}
            // Grouping by schedule, the cadence IS the group's contract, so it belongs in the header: a
            // group of daily flows that missed six days reads differently from a weekly one that missed six.
            caption={groupBy === "schedule" ? (rows[0].cron ?? undefined) : undefined}
            rows={rows}
          />
        ),
      }],
    };
  }, [groupBy]);

  const controls = (
    <div className="flex flex-wrap items-center gap-4">
      <Button
        variant="outline"
        size="sm"
        onClick={recheck}
        disabled={query.isFetching}
        aria-label="Recheck data streams"
        data-testid="datastreams-recheck"
      >
        <RefreshCw className={query.isFetching ? "animate-spin" : undefined} />
        Recheck
      </Button>
      <div className="flex items-center gap-2">
        <Switch
          id="include-backfills"
          checked={includeBackfills}
          onCheckedChange={setIncludeBackfills}
          data-testid="datastreams-include-backfills"
        />
        <Label htmlFor="include-backfills" className="text-xs font-normal text-muted-foreground">
          Include backfills
        </Label>
      </div>
      <div className="flex items-center gap-2">
        <Switch
          id="include-unscheduled"
          checked={includeUnscheduled}
          onCheckedChange={setIncludeUnscheduled}
          data-testid="datastreams-include-unscheduled"
        />
        <Label htmlFor="include-unscheduled" className="text-xs font-normal text-muted-foreground">
          Include unscheduled
        </Label>
      </div>
      <ToggleGroup
        type="single"
        value={String(days)}
        onValueChange={(value) => { if (value !== "") { setDays(Number(value)); } }}
        variant="outline"
        size="sm"
        data-testid="datastreams-window-toggle"
      >
        {windowChoices.map((choice) => (
          <ToggleGroupItem key={choice.days} value={String(choice.days)} className="px-2.5 text-xs">
            {choice.label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>
    </div>
  );

  if (query.isError) {
    return (
      <Page data-testid="page-datastreams">
        <PageHeader title="Data streams" />
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const board = query.data;

  if (board === undefined) {
    return (
      <Page data-testid="page-datastreams">
        <PageHeader title="Data streams" actions={controls} />
        <div className={kpiGridClass}>
          {Array.from({ length: 5 }, (_, i) => <Skeleton key={`kpi-${i}`} className="h-[88px] rounded-lg" />)}
        </div>
        <Skeleton className="h-96 rounded-lg" />
      </Page>
    );
  }

  const truncated = board.totalStreams > board.analyzedStreams;

  return (
    <Page data-testid="page-datastreams">
      <PageHeader
        title="Data streams"
        subtitle={(
          <>
            {(board.scope === "source"
              ? "Has the vendor delivered? Data arriving from outside the estate"
              : board.scope === "internal"
                ? "Have we processed it? Tables we derive from the archive onwards"
                : "Vendor deliveries and our own processing together") +
              `, over the last ${days} days` +
              (board.includeBackfills ? " (backfills counted as normal traffic)" : "")}
            {/* When the verdicts were computed, because "this is stale" is otherwise indistinguishable from
                "nothing changed" after a run. */}
            {" · checked "}
            <RelativeTime value={board.asOfUtc} absolute={false} />
          </>
        )}
        actions={controls}
      />

      {/* The split is the primary control, so it sits above the numbers it changes rather than beside the
          window picker: every count below it is scoped to whichever side is selected. */}
      <ToggleGroup
        type="single"
        value={board.scope}
        onValueChange={(value) => { if (value !== "") { setScope(value); } }}
        variant="outline"
        size="sm"
        className="self-start"
        data-testid="datastreams-scope"
      >
        <ToggleGroupItem value="source" className="px-3 text-xs">
          Vendor deliveries ({board.sourceStreams})
        </ToggleGroupItem>
        <ToggleGroupItem value="internal" className="px-3 text-xs">
          Our processing ({board.internalStreams})
        </ToggleGroupItem>
        <ToggleGroupItem value="all" className="px-3 text-xs">
          Everything ({board.sourceStreams + board.internalStreams})
        </ToggleGroupItem>
      </ToggleGroup>

      <div className={kpiGridClass}>
        <KpiCard
          label="Stopped"
          value={board.stalledCount}
          caption="no data at all"
          color={board.stalledCount > 0 ? "error" : "success"}
          testId="kpi-streams-stalled"
        />
        <KpiCard
          label="Missing data"
          value={board.degradedCount}
          caption="skipping loads"
          color={board.degradedCount > 0 ? "warning" : undefined}
          testId="kpi-streams-degraded"
        />
        <KpiCard label="Worth a look" value={board.watchCount} caption="one weak signal" testId="kpi-streams-watch" />
        <KpiCard label="OK" value={board.healthyCount} caption="loading on pattern" testId="kpi-streams-healthy" />
        <KpiCard
          label="Backfills"
          value={board.excludedBackfillRuns}
          caption={board.includeBackfills ? "counted" : "left out"}
          testId="kpi-streams-backfills"
        />
      </div>

      <Card>
        <CardHeader className="pb-2">
          <CardTitle className="text-sm">
            {board.analyzedStreams} table{board.analyzedStreams === 1 ? "" : "s"} checked
            {truncated && ` of ${board.totalStreams} (capped)`}
            {!board.includeUnscheduled && board.unscheduledStreams > 0 && (
              <span className="ml-2 font-normal text-muted-foreground">
                {board.unscheduledStreams} on no schedule, left out
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <ToggleGroup
              type="single"
              value={status}
              onValueChange={setStatus}
              variant="outline"
              size="sm"
              data-testid="datastreams-status-filter"
            >
              {statusFilters.map((filter) => (
                <ToggleGroupItem key={filter.value || "all"} value={filter.value} className="px-2.5 text-xs">
                  {filter.label}
                </ToggleGroupItem>
              ))}
            </ToggleGroup>
            <ToggleGroup
              type="single"
              value={groupBy}
              onValueChange={(value) => { if (value !== "") { setGroupBy(value); } }}
              variant="outline"
              size="sm"
              data-testid="datastreams-group-by"
            >
              {groupChoices.map((choice) => (
                <ToggleGroupItem key={choice.value} value={choice.value} className="px-2.5 text-xs">
                  {choice.label}
                </ToggleGroupItem>
              ))}
            </ToggleGroup>
          </div>

          {board.streams.length === 0 ? (
            <EmptyState
              icon={<Activity />}
              title="Nothing to show"
              description={
                status !== ""
                  ? "Every table checked is in a different state. Clear the filter to see them."
                  : board.scope === "source"
                    ? "No stream brings data in from outside the estate in this window. Try Our processing."
                    : "No flow has run inside this window, so there is nothing to check."
              }
            />
          ) : (
            <DataTable
              columns={columns}
              rows={board.streams}
              rowKey={(s) => s.pipelineId}
              onRowClick={(s) => setDrill({ pipelineId: s.pipelineId, flowName: s.flowName })}
              grouping={grouping}
              emptyMessage="No tables to show."
              // Below this the columns would compress and clip their content instead of the card scrolling.
              minWidth={1140}
              data-testid="datastreams-table"
            />
          )}
        </CardContent>
      </Card>

      <StreamDetailSheet
        pipelineId={drill?.pipelineId ?? null}
        flowName={drill?.flowName ?? null}
        windowDays={days}
        includeBackfills={includeBackfills}
        onClose={() => setDrill(null)}
      />
    </Page>
  );
}
