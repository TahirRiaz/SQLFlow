import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Activity, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { DataStream, StreamProfile, StreamStatus } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column, type TableGrouping } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { pollingInterval } from "../../hooks/usePolling";
import { StreamDetailSheet } from "./StreamDetailSheet";
import { StreamSparkline } from "./StreamSparkline";
import { StreamStatusBadge, StreamStatusCounts } from "./StreamStatusBadge";
import {
  cycleHint, evidence, expectedRhythm, finding, formatDays, formatRows, stageLabels, stageMeaning,
  statusCaptions, statusLabels,
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

const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-3";

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

/** The verdict tiles in the order the board ranks them, each doubling as the filter for its own count. */
const verdictTiles: { status: StreamStatus; color?: "error" | "warning" | "success" }[] = [
  { status: "stalled", color: "error" },
  { status: "degraded", color: "warning" },
  { status: "watch" },
  { status: "healthy", color: "success" },
  { status: "insufficient-history" },
];

/** The days a group's data last arrived, across every stream in it: the newest load anywhere in the group,
 * or null when no stream in it has ever loaded. */
function groupLastLoad(rows: DataStream[]): number | null {
  let newest: number | null = null;
  for (const row of rows) {
    const days = row.profile.daysSinceLastLoad;
    if (days !== null && (newest === null || days < newest)) {
      newest = days;
    }
  }

  return newest;
}

/**
 * One collapsed group: the schedule (or source), how many of its tables are in each verdict, and when data
 * last arrived anywhere in it. A source with fifty objects is one line here, and the line is meant to answer
 * "do I need to open this" without opening it: the verdict counts say how bad, the last-arrival says how
 * current, and the cron (when grouping by schedule) says what the group promised.
 */
function GroupHeader({ label, cron, timezone, rows }: {
  label: string;
  cron?: string | null;
  timezone?: string | null;
  rows: DataStream[];
}) {
  const lastLoad = groupLastLoad(rows);
  return (
    <div className="flex min-w-0 flex-1 items-center gap-3">
      <span className="shrink-0 text-[13px] font-medium">{label}</span>
      {cron !== undefined && cron !== null && (
        <span
          className="shrink-0 font-mono text-[11px] text-muted-foreground"
          title={timezone === null || timezone === undefined ? undefined : `Cron in ${timezone}`}
        >
          {cron}
        </span>
      )}
      <StreamStatusCounts rows={rows} />
      <span className="shrink-0 text-[11px] text-muted-foreground">
        {rows.length} table{rows.length === 1 ? "" : "s"}
      </span>
      <span className="truncate text-[11px] text-muted-foreground">
        {lastLoad === null ? "no data ever" : `data last arrived ${formatDays(lastLoad).toLowerCase()}`}
      </span>
    </div>
  );
}

/** The line under a missed-days count: who owns the misses, and whether the stream's own history predicts
 * that many anyway. */
function missedCaption(profile: StreamProfile): string {
  if (profile.unexpectedNullDays === 0) {
    return "loaded every time";
  }

  const parts: string[] = [];
  if (profile.emptyRunDays > 0) {
    parts.push(`${profile.emptyRunDays} ran empty`);
  }

  if (profile.noRunDays > 0) {
    parts.push(`${profile.noRunDays} no run`);
  }

  if (profile.predictedNullDays >= 0.5) {
    parts.push(`usually ${Math.round(profile.predictedNullDays)}`);
  }

  return parts.join(" · ");
}

/**
 * DataStream anomaly detection: which tables have stopped receiving data.
 *
 * The board is split by WHO a finding belongs to, because that is the first thing anyone needs to know and
 * mixing the two sides makes both unreadable. A vendor that did not deliver and a transformation of ours that
 * broke are different incidents with different owners, and one quiet upstream would otherwise light up its
 * whole downstream chain as a dozen separate findings that are all the same finding. Vendor deliveries are the
 * default view; our own processing is one click away, and so is everything at once.
 *
 * Below the split, the verdict tiles are the filter: the number on a tile says how many tables are in that
 * state, and clicking it shows which. One vocabulary, one control, rather than a row of counts and a separate
 * row of chips carrying the same five words.
 *
 * Each row then reads left to right in the order an operator asks the questions. Which table, and at which
 * stage (a source-side stream failing at "Vendor fetch" means nothing arrived from them; the same stream
 * failing at "File ingestion" or "Load to archive" means it arrived and we did not take it in). What the
 * verdict is, and what exactly is wrong, with its number and unit in the sentence and the evidence behind it
 * underneath. Then the proof: the last two weeks drawn as bars against the expectation, so "less data than
 * usual" is a shape rather than a claim; when data last arrived against how often it is expected; and how many
 * expected days went by with nothing, always as "n of m days" so a zero can never be mistaken for zero rows.
 * The statistics behind each of those live one click away in the detail sheet.
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
      width: 270,
      render: (s) => (
        <div className="flex min-w-0 flex-col" style={{ maxWidth: 250 }}>
          <span className="truncate font-mono text-xs" title={s.flowName}>{s.flowName}</span>
          <span className="truncate text-[11px] text-muted-foreground" title={s.targetObject ?? undefined}>
            {/* schema.table, because the bare name does not distinguish a staging copy from the archive one
                it feeds; the full three-part name is on hover. The stage rides beside it because it is the
                sharper form of "whose problem": the same silence is theirs at the fetch and ours at the load. */}
            {shortTarget(s.targetObject) ?? s.batch ?? "unknown target"}
            {" · "}
            <span title={stageMeaning[s.stage]}>{stageLabels[s.stage]}</span>
          </span>
        </div>
      ),
    },
    {
      id: "status",
      header: "Status",
      width: 120,
      render: (s) => <StreamStatusBadge status={s.status} severity={s.severity} />,
    },
    {
      id: "finding",
      header: "What's wrong",
      width: 340,
      render: (s) => (
        <div className="flex min-w-0 flex-col gap-0.5" style={{ maxWidth: 330 }}>
          {/* The headline with its number in it; the whole reasoning is one hover away, because a paragraph
              in a cell is unreadable and a category alone is unhelpful. */}
          <RichTooltip body={s.summary} title="Finding">
            <span className="block truncate text-[13px]">{finding(s)}</span>
          </RichTooltip>
          <span className="truncate text-[11px] text-muted-foreground">{evidence(s)}</span>
        </div>
      ),
    },
    {
      id: "recent",
      header: "Last 14 days",
      width: 170,
      render: (s) => {
        const typical = s.profile.pattern.typicalRows > 0
          ? `~${formatRows(s.profile.pattern.typicalRows, true)} rows per load`
          : "no typical size yet";
        // The recurring delivery the rhythm alone cannot express. Without it a row reads "~9k rows" for a
        // vendor that also ships double that every fortnight, and every fortnight the board would have to
        // explain itself.
        const cycle = cycleHint(s.profile.pattern);
        return (
          <div className="flex flex-col gap-1">
            <StreamSparkline sparkline={s.sparkline} />
            <span
              className="truncate text-[11px] text-muted-foreground"
              style={{ maxWidth: 150 }}
              title={s.profile.pattern.cycle?.description ?? s.profile.pattern.description}
            >
              {typical}
              {cycle !== null && `, ${cycle}`}
            </span>
          </div>
        );
      },
    },
    {
      id: "lastLoad",
      header: "Last data",
      width: 135,
      render: (s) => (
        <div className="flex flex-col">
          <span
            className={cn(
              "font-mono text-xs tabular-nums",
              (s.category === "stalled" || s.category === "failing") && "text-destructive",
            )}
          >
            {formatDays(s.profile.daysSinceLastLoad)}
          </span>
          <span className="text-[11px] text-muted-foreground">
            {expectedRhythm(s.profile.pattern, s.profile.expectedGapDays)}
          </span>
        </div>
      ),
    },
    {
      id: "missed",
      header: "Missed days",
      width: 135,
      align: "right",
      render: (s) => {
        const profile = s.profile;
        if (profile.expectedDays === 0) {
          return (
            <span className="text-xs text-muted-foreground" title="No fixed rhythm, so no day was expected">
              -
            </span>
          );
        }

        const missed = profile.unexpectedNullDays;
        return (
          <div className="flex flex-col items-end">
            {/* Always "n of m", never a bare number: the m says these are days and says how many there were
                to miss, which is what makes "0 of 30" a good thing and "3 of 4" a bad one. */}
            <span
              className={cn(
                "font-mono text-xs tabular-nums",
                missed === 0 ? "text-muted-foreground" : missed > profile.predictedNullDays && "text-destructive",
              )}
            >
              {missed} of {profile.expectedDays}
            </span>
            <span className="text-[11px] text-muted-foreground">{missedCaption(profile)}</span>
          </div>
        );
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
            cron={groupBy === "schedule" ? rows[0].cron : undefined}
            timezone={groupBy === "schedule" ? rows[0].timezone : undefined}
            rows={rows}
          />
        ),
      }],
    };
  }, [groupBy]);

  const board = query.data;

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
          {/* The count sits on the control that changes it, so the number and the switch read as one
              thing: how many runs this decision is about. */}
          {board !== undefined && board.excludedBackfillRuns > 0 && (
            <span className="ml-1 font-mono tabular-nums" data-testid="kpi-streams-backfills-value">
              ({board.excludedBackfillRuns} {board.includeBackfills ? "counted" : "left out"})
            </span>
          )}
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
          {board !== undefined && !board.includeUnscheduled && board.unscheduledStreams > 0 && (
            <span className="ml-1 font-mono tabular-nums">({board.unscheduledStreams} left out)</span>
          )}
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

  if (board === undefined) {
    return (
      <Page data-testid="page-datastreams">
        <PageHeader title="Data streams" actions={controls} />
        <Skeleton className="h-8 w-96 rounded-md" />
        <div className={kpiGridClass}>
          {verdictTiles.map((tile) => <Skeleton key={tile.status} className="h-[88px] rounded-lg" />)}
        </div>
        <Skeleton className="h-96 rounded-lg" />
      </Page>
    );
  }

  const truncated = board.totalStreams > board.analyzedStreams;
  const countOf: Record<StreamStatus, number> = {
    stalled: board.stalledCount,
    degraded: board.degradedCount,
    watch: board.watchCount,
    healthy: board.healthyCount,
    "insufficient-history": board.insufficientHistoryCount,
  };

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

      {/* The verdict counts, and the filter, as one control: the tile that counts a state is the tile that
          shows it. Clicking the selected tile again shows everything. The counts cover every analysed
          stream whatever is selected, so the tiles never lose their meaning to their own filter. */}
      <div className={kpiGridClass} role="group" aria-label="Filter by verdict" data-testid="datastreams-status-filter">
        {verdictTiles.map((tile) => (
          <KpiCard
            key={tile.status}
            label={statusLabels[tile.status]}
            value={countOf[tile.status]}
            caption={statusCaptions[tile.status]}
            color={tile.color !== undefined && countOf[tile.status] > 0 ? tile.color : undefined}
            selected={status === tile.status}
            onClick={() => setStatus(status === tile.status ? "" : tile.status)}
            testId={`kpi-streams-${tile.status === "insufficient-history" ? "too-new" : tile.status}`}
          />
        ))}
      </div>

      <Card>
        <CardHeader className="pb-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <CardTitle className="text-sm">
              {status === ""
                ? `${board.analyzedStreams} table${board.analyzedStreams === 1 ? "" : "s"} checked`
                : `${board.streams.length} of ${board.analyzedStreams} tables: ${statusLabels[status as StreamStatus].toLowerCase()}`}
              {truncated && ` of ${board.totalStreams} (capped)`}
            </CardTitle>
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
        </CardHeader>
        <CardContent>
          {board.streams.length === 0 ? (
            <EmptyState
              icon={<Activity />}
              title="Nothing to show"
              description={
                status !== ""
                  ? "Every table checked is in a different state. Click the selected tile again to see them all."
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
              minWidth={1170}
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
