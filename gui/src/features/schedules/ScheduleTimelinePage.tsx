import { useMemo, useRef, useState } from "react";
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
import { FilterBar } from "../../components/FilterBar";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { pollingInterval } from "../../hooks/usePolling";
import { formatDurationSeconds } from "../../lib/time";
import { ScheduleTimelineChart, statusTone, type ScheduleTimelineHandle, type StatusTone } from "./ScheduleTimelineChart";
import { buildRows, computeStats, DEFAULT_RANGE_KEY, RANGE_PRESETS, rangeByKey } from "./timeline";

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

/** The Schedules timeline: a day-by-day Gantt of when each schedule's flow actually ran, with the estate's
 * cadence rolled up into an insight strip (busiest hour, quietest free window, success rate). */
export default function ScheduleTimelinePage() {
  const [rangeKey, setRangeKey] = useState<string>(DEFAULT_RANGE_KEY);
  const [zoomed, setZoomed] = useState(false);
  const chartRef = useRef<ScheduleTimelineHandle>(null);
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

  const successColor = stats?.successRate == null
    ? undefined
    : stats.successRate >= 90
      ? "success" as const
      : stats.successRate >= 50
        ? "warning" as const
        : "error" as const;

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

    return (
      <ScheduleTimelineChart
        ref={chartRef}
        rows={rows}
        windowStartMs={runsQuery.data.fromMs}
        windowEndMs={runsQuery.data.toMs}
        nowMs={runsQuery.data.toMs}
        onZoomChange={setZoomed}
      />
    );
  };

  return (
    <Page data-testid="page-schedule-timeline">
      <PageHeader
        title="Schedule timeline"
        subtitle="When each schedule's flow actually ran, across the selected window."
        actions={(
          <Button variant="outline" size="sm" asChild data-testid="timeline-to-table">
            <RouterLink to="/schedules">
              <Rows3 />
              Table
            </RouterLink>
          </Button>
        )}
      />

      <FilterBar>
        {zoomed && (
          <Button variant="outline" size="sm" onClick={() => chartRef.current?.reset()} data-testid="timeline-reset-zoom">
            Reset zoom
          </Button>
        )}
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
      </FilterBar>

      {stats !== null && runsQuery.data !== undefined && (
        <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
          <KpiCard label="Schedules" value={`${stats.activeScheduleCount}/${stats.scheduleCount}`} testId="timeline-insight" />
          <KpiCard label="Runs" value={String(stats.runCount)} testId="timeline-insight" />
          <KpiCard
            label="Success"
            value={stats.successRate == null ? "-" : `${stats.successRate}%`}
            color={successColor}
            testId="timeline-insight"
          />
          <KpiCard
            label="Avg duration"
            value={stats.avgDurationSeconds == null ? "-" : formatDurationSeconds(stats.avgDurationSeconds)}
            testId="timeline-insight"
          />
          <KpiCard label="Busiest hour" value={stats.busiestHour ?? "-"} testId="timeline-insight" />
          <KpiCard
            label="Best window"
            value={stats.bestWindow?.range ?? "-"}
            caption={stats.bestWindow === null ? undefined : `${stats.bestWindow.freeHours}h free`}
            testId="timeline-insight"
          />
        </div>
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
          <span className="ml-auto">
            Click a day to drill in · double-click the chart to zoom · scroll to zoom · drag to pan
          </span>
        </div>
      )}
    </Page>
  );
}
