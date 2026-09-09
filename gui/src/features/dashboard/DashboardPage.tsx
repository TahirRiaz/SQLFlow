import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipProps,
} from "recharts";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { summaryApi } from "../../api/endpoints";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { pollingInterval } from "../../hooks/usePolling";
import { useThemeMode } from "../../theme/ThemeModeContext";

// One even grid for the KPI cards (and their loading skeletons), so the headline numbers read as a designed
// row instead of a ragged flex wrap.
const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4";

/** The run-state distribution's hover readout, on the popover surface like every other floating layer. */
function RunStateTooltip({ active, payload, label }: TooltipProps<number, string>) {
  if (active !== true || payload === undefined || payload.length === 0) {
    return null;
  }

  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-medium">{label}</div>
      <div className="mt-0.5 font-mono tabular-nums">{payload[0].value} runs</div>
    </div>
  );
}

/** The operator's landing page: headline counts linking into each area plus the run-state distribution. */
export default function DashboardPage() {
  const { mode } = useThemeMode();
  const query = useQuery({
    queryKey: ["summary"],
    queryFn: summaryApi.get,
    refetchInterval: pollingInterval(10000),
  });

  // Recharts needs literal color values, so the reserved status tokens (DESIGN.md 3.2) and the muted
  // axis/grid tokens are read off the root element with getComputedStyle: the chart uses the exact same
  // custom properties as the rest of the app, re-read whenever the theme flips. Run states are status
  // colors by definition and never take chart-series slots.
  const chartColors = useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      queued: token("--warning"),
      running: token("--info"),
      succeeded: token("--success"),
      failed: token("--destructive"),
      cancelled: token("--muted-foreground"),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      cursor: token("--muted"),
    };
    // The mode value is the re-read trigger: the tokens themselves come from the stylesheet.
  }, [mode]);

  if (query.isError) {
    return (
      <Page data-testid="page-dashboard">
        <PageHeader title="Dashboard" />
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{String(query.error)}</p>}
      </Page>
    );
  }

  const dashboard = query.data;
  if (dashboard === undefined) {
    return (
      <Page data-testid="page-dashboard">
        <PageHeader title="Dashboard" />
        <div className={kpiGridClass}>
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={`kpi-skeleton-${i}`} className="h-[104px] rounded-lg" />
          ))}
        </div>
        <Skeleton className="h-[372px] rounded-lg" />
      </Page>
    );
  }

  const allNodesOnline = dashboard.nodesTotal > 0 && dashboard.nodesOnline === dashboard.nodesTotal;
  // Each run state wears its reserved status color; the x-axis tick names the state, so color never
  // carries the identity alone.
  const runStates = [
    { state: "queued", count: dashboard.runs.queued, color: chartColors.queued },
    { state: "running", count: dashboard.runs.running, color: chartColors.running },
    { state: "succeeded", count: dashboard.runs.succeeded, color: chartColors.succeeded },
    { state: "failed", count: dashboard.runs.failed, color: chartColors.failed },
    { state: "cancelled", count: dashboard.runs.cancelled, color: chartColors.cancelled },
  ];
  const noRuns = runStates.every((entry) => entry.count === 0);

  return (
    <Page data-testid="page-dashboard">
      <PageHeader
        title="Dashboard"
        subtitle={(
          <span data-testid="dashboard-as-of">
            As of <RelativeTime value={dashboard.asOfUtc} />
          </span>
        )}
      />

      <div className={kpiGridClass}>
        <KpiCard label="Repos" value={dashboard.repos} linkTo="/repos" testId="kpi-repos" />
        <KpiCard
          label="Pipelines"
          value={`${dashboard.activePipelines}/${dashboard.pipelines}`}
          caption="active/total"
          linkTo="/pipelines"
          testId="kpi-pipelines"
        />
        <KpiCard
          label="Nodes"
          value={`${dashboard.nodesOnline}/${dashboard.nodesTotal}`}
          caption="online/total"
          linkTo="/nodes"
          color={allNodesOnline ? "success" : undefined}
          testId="kpi-nodes"
        />
        <KpiCard
          label="Schedules"
          value={dashboard.schedulesEnabled}
          caption={`enabled (${dashboard.schedulesPaused} paused)`}
          linkTo="/schedules"
          testId="kpi-schedules"
        />
        <KpiCard
          label="Repo sources"
          value={dashboard.repoSources}
          caption={`${dashboard.repoSourcesWithErrors} with errors`}
          linkTo="/repos"
          color={dashboard.repoSourcesWithErrors > 0 ? "error" : undefined}
          testId="kpi-repo-sources"
        />
        <KpiCard label="Runs last 24h" value={dashboard.runs.last24h} linkTo="/runs" testId="kpi-runs-24h" />
      </div>

      <Card className="gap-0 rounded-lg py-4" data-testid="runs-by-state-card">
        <CardHeader className="px-4">
          <CardTitle className="text-base font-medium">Runs by state</CardTitle>
        </CardHeader>
        <CardContent className="h-80 px-4 pt-3">
          {noRuns ? (
            <EmptyState
              title="No runs recorded yet"
              description="Trigger a run and its state distribution appears here."
            />
          ) : (
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={runStates} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
                <CartesianGrid vertical={false} stroke={chartColors.grid} />
                <XAxis
                  dataKey="state"
                  stroke={chartColors.axis}
                  tick={{ fill: chartColors.axis, fontSize: 12 }}
                  axisLine={{ stroke: chartColors.grid }}
                  tickLine={false}
                />
                <YAxis
                  allowDecimals={false}
                  stroke={chartColors.axis}
                  tick={{ fill: chartColors.axis, fontSize: 12 }}
                  axisLine={{ stroke: chartColors.grid }}
                  tickLine={false}
                />
                <Tooltip cursor={{ fill: chartColors.cursor }} content={<RunStateTooltip />} />
                <Bar dataKey="count" name="Runs" radius={[4, 4, 0, 0]} maxBarSize={48}>
                  {runStates.map((entry) => (
                    <Cell key={entry.state} fill={entry.color} />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>
    </Page>
  );
}
