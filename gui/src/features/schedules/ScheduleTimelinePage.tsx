import { useMemo, useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Rows3 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { runApi, scheduleApi } from "../../api/endpoints";
import type { RunStatus, RunSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { SummaryStrip, type SummaryCell } from "../../components/SummaryStrip";
import { pollingInterval } from "../../hooks/usePolling";
import { formatDurationSeconds } from "../../lib/time";
import { ScheduleTimelineChart } from "./ScheduleTimelineChart";
import {
  buildRows, computeStats, DEFAULT_RANGE_KEY, RANGE_PRESETS, rangeByKey, statusTone, type StatusTone, type TimelineStats,
} from "./timeline";

const DAY_MS = 86_400_000;

/** Pages the windowed runs list until the whole window is in hand (bounded so a huge estate can't run away). */
async function fetchRunsInWindow(fromIso: string, toIso: string): Promise<RunSummary[]> {
  const pageSize = 500;
  const out: RunSummary[] = [];
  for (let page = 1; page <= 40; page += 1) {
    const result = await runApi.list({ from: fromIso, to: toIso, page, pageSize });
    out.push(...result.items);
    if (out.length >= result.total || result.items.length < pageSize) {
      break;
    }
  }

  return out;
}

const LEGEND: { status: RunStatus; label: string }[] = [
  { status: "succeeded", label: "succeeded" },
  { status: "failed", label: "failed" },
  { status: "running", label: "running" },
  { status: "queued", label: "queued" },
  { status: "cancelled", label: "cancelled" },
  { status: "skipped", label: "skipped" },
];

/** The legend swatch class for each status tone: the semantic utilities behind the same DESIGN.md 3.2 mapping
 * the chart reads via tokens, so the legend and the bars can never disagree. */
const TONE_SWATCH: Record<StatusTone, string> = {
  success: "bg-success",
  destructive: "bg-destructive",
  info: "bg-info",
  warning: "bg-warning",
  muted: "bg-muted-foreground",
};

/** The figures a new schedule is planned against, on one line above the chart: where the day is free, where it is
 * busiest and how long a run takes first, then how the window went. A success rate below 90% speaks up; a healthy
 * one stays quiet. */
function summaryCells(stats: TimelineStats): SummaryCell[] {
  const free = stats.bestWindow;
  const busiest = stats.busiestHour;
  const rate = stats.successRate;
  return [
    {
      label: "Free window",
      value: free?.range ?? "none",
      caption: free === null ? "something runs every hour" : `${free.freeHours}h, nothing running`,
      testId: "timeline-insight",
    },
    {
      label: "Busiest hour",
      value: busiest?.range ?? "-",
      caption: busiest === null ? undefined : `${busiest.runCount} ${busiest.runCount === 1 ? "run starts" : "runs start"} then`,
      testId: "timeline-insight",
    },
    {
      label: "Average run",
      value: stats.avgDurationSeconds == null ? "-" : formatDurationSeconds(stats.avgDurationSeconds),
      testId: "timeline-insight",
    },
    {
      label: "Runs",
      value: String(stats.runCount),
      caption: rate == null ? undefined : (
        <span className={cn(rate < 50 ? "text-destructive" : rate < 90 && "text-warning")}>{`${rate}% succeeded`}</span>
      ),
      testId: "timeline-insight",
    },
    {
      label: "Active schedules",
      value: `${stats.activeScheduleCount} of ${stats.scheduleCount}`,
      testId: "timeline-insight",
    },
  ];
}

/** The Schedules timeline: what runs when, so a new schedule can be put where the estate has room. A day-by-day Gantt
 * of each schedule's runs, opened on the latest days with the free window shaded, under one line of the figures that
 * plan it (free window, busiest hour, average run). */
export default function ScheduleTimelinePage() {
  const [rangeKey, setRangeKey] = useState<string>(DEFAULT_RANGE_KEY);
  const range = rangeByKey(rangeKey);

  const schedulesQuery = useQuery({
    queryKey: ["schedule-timeline", "schedules"],
    queryFn: () => scheduleApi.list({ page: 1, pageSize: 200 }),
    refetchInterval: pollingInterval(30000),
  });

  const runsQuery = useQuery({
    queryKey: ["schedule-timeline", "runs", rangeKey],
    queryFn: async () => {
      const toMs = Date.now();
      const fromMs = toMs - range.days * DAY_MS;
      const runs = await fetchRunsInWindow(new Date(fromMs).toISOString(), new Date(toMs).toISOString());
      return { runs, fromMs, toMs };
    },
    refetchInterval: pollingInterval(30000),
  });

  const schedules = schedulesQuery.data?.items;
  const rows = useMemo(
    () => (schedules !== undefined && runsQuery.data !== undefined ? buildRows(schedules, runsQuery.data.runs) : []),
    [schedules, runsQuery.data],
  );
  const stats = useMemo(
    () => (schedules !== undefined ? computeStats(schedules, rows) : null),
    [schedules, rows],
  );

  const renderBody = () => {
    if (schedulesQuery.isError) {
      return isApiError(schedulesQuery.error)
        ? <CorrelationError error={schedulesQuery.error} />
        : <p className="text-[13px] text-destructive">{String(schedulesQuery.error)}</p>;
    }

    if (schedules === undefined) {
      return <Skeleton className="h-[420px] w-full rounded-lg" />;
    }

    if (schedules.length === 0) {
      return (
        <EmptyState
          title="No schedules yet"
          description="Create a schedule and its runs appear here, day by day."
          action={(
            <Button size="sm" asChild>
              <RouterLink to="/schedules">Go to schedules</RouterLink>
            </Button>
          )}
          data-testid="timeline-empty-schedules"
        />
      );
    }

    if (runsQuery.isError) {
      return isApiError(runsQuery.error)
        ? <CorrelationError error={runsQuery.error} />
        : <p className="text-[13px] text-destructive">{String(runsQuery.error)}</p>;
    }

    if (runsQuery.data === undefined) {
      return <Skeleton className="h-[420px] w-full rounded-lg" />;
    }

    if (stats !== null && stats.runCount === 0) {
      return (
        <EmptyState
          title={`No runs in the ${range.label.toLowerCase()}`}
          description="Nothing fired in this window. Widen the range, or trigger a run to see it land on the timeline."
          action={rangeKey !== "30d" ? (
            <Button variant="outline" size="sm" onClick={() => setRangeKey("30d")}>Show last 30 days</Button>
          ) : undefined}
          data-testid="timeline-empty-runs"
        />
      );
    }

    // Keyed by range: a new range opens on its latest days again, while a refetch of the same range keeps the view.
    return (
      <ScheduleTimelineChart
        key={rangeKey}
        rows={rows}
        windowStartMs={runsQuery.data.fromMs}
        windowEndMs={runsQuery.data.toMs}
        nowMs={runsQuery.data.toMs}
        freeWindow={stats?.bestWindow ?? null}
      />
    );
  };

  return (
    <Page data-testid="page-schedule-timeline">
      <PageHeader
        title="Schedule timeline"
        subtitle="What runs when, to plan where a new schedule fits."
        actions={(
          <>
            <ToggleGroup
              type="single"
              variant="outline"
              size="sm"
              value={rangeKey}
              onValueChange={(next) => {
                if (next !== "") {
                  setRangeKey(next);
                }
              }}
              aria-label="History range"
              data-testid="timeline-range"
            >
              {RANGE_PRESETS.map((preset) => (
                <ToggleGroupItem key={preset.key} value={preset.key} className="h-8 px-2.5 text-xs" aria-label={preset.label}>
                  {preset.key}
                </ToggleGroupItem>
              ))}
            </ToggleGroup>
            <Button variant="outline" size="sm" asChild data-testid="timeline-to-table">
              <RouterLink to="/schedules">
                <Rows3 />
                Table
              </RouterLink>
            </Button>
          </>
        )}
      />

      {stats !== null && stats.runCount > 0 && (
        <SummaryStrip cells={summaryCells(stats)} minCellWidth={150} data-testid="timeline-summary" />
      )}

      <div className="rounded-lg border bg-card p-4">{renderBody()}</div>

      {schedules !== undefined && schedules.length > 0 && stats !== null && stats.runCount > 0 && (
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-xs text-muted-foreground">
          {LEGEND.map((entry) => (
            <span key={entry.status} className="flex items-center gap-1.5">
              <span className={cn("size-3 rounded-[3px]", TONE_SWATCH[statusTone(entry.status)])} />
              {entry.label}
            </span>
          ))}
          <span className="flex items-center gap-1.5">
            <span className="size-3 rounded-full border-[1.5px] border-dashed border-primary bg-primary/10" />
            next fire
          </span>
          {stats.bestWindow !== null && (
            <span className="flex items-center gap-1.5">
              {/* The band as the chart draws it: a tenth of the primary over the card, not over the page behind the legend. */}
              <span className="relative size-3 overflow-hidden rounded-[3px] border bg-card">
                <span className="absolute inset-0 bg-primary/10" />
              </span>
              free window
            </span>
          )}
          <span className="ml-auto">
            Scroll to zoom · drag to pan · click a date or double-click the chart to open that day
          </span>
        </div>
      )}
    </Page>
  );
}
