import { Suspense, useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Rectangle,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type BarShapeProps,
  type TooltipContentProps,
} from "recharts";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { BandCursor, chartBehaviour, chartLayers } from "@/lib/charts";
import { isApiError } from "../../api/client";
import { summaryApi } from "../../api/endpoints";
import type { Dashboard } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { pollingInterval } from "../../hooks/usePolling";
import { moduleDashboardTiles, moduleHomeSections } from "../../modules/registry";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { HomeHero } from "./HomeHero";
import { RecentRuns } from "./RecentRuns";

// One even grid for the KPI cards (and their loading skeletons), so the headline numbers read as a designed
// row instead of a ragged flex wrap.
const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4";

/** One bar of the run-state distribution: a state, its run count, and the state's reserved status color. */
interface RunStateDatum {
  state: string;
  count: number;
  color: string;
}

/** A run state's bar, filled with that state's own color. */
function RunStateBar(props: BarShapeProps) {
  return <Rectangle {...props} fill={(props.payload as RunStateDatum).color} />;
}

/** The run-state distribution's hover readout, on the popover surface like every other floating layer. */
function RunStateTooltip({ active, payload, label }: TooltipContentProps) {
  if (active !== true || payload.length === 0) {
    return null;
  }

  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-medium">{label}</div>
      <div className="mt-0.5 font-mono tabular-nums">{payload[0].value} runs</div>
    </div>
  );
}

/** The headline counts, each linking into its area, with the tiles the modules add after SQLFlow's own. */
function Glance({ dashboard }: { dashboard: Dashboard }) {
  const allNodesOnline = dashboard.nodesTotal > 0 && dashboard.nodesOnline === dashboard.nodesTotal;
  return (
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
      {moduleDashboardTiles().map(({ id, component: Tile }) => <Tile key={id} />)}
    </div>
  );
}

/**
 * The page a person lands on (DESIGN.md 12): the headline counts linking into each area, the hero with the assistant's
 * question box, the sections the modules add about their own product, and the newest runs beside the run-state
 * distribution.
 */
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

  const dashboard = query.data;
  // Each run state wears its reserved status color; the x-axis tick names the state, so color never
  // carries the identity alone.
  const runStates: RunStateDatum[] = dashboard === undefined ? [] : [
    { state: "queued", count: dashboard.runs.queued, color: chartColors.queued },
    { state: "running", count: dashboard.runs.running, color: chartColors.running },
    { state: "succeeded", count: dashboard.runs.succeeded, color: chartColors.succeeded },
    { state: "failed", count: dashboard.runs.failed, color: chartColors.failed },
    { state: "cancelled", count: dashboard.runs.cancelled, color: chartColors.cancelled },
  ];
  const noRuns = runStates.every((entry) => entry.count === 0);

  return (
    <Page data-testid="page-dashboard">
      {/* The headline counts open the page on their own, without a heading: the cards name themselves. */}
      <section data-testid="home-glance">
        {query.isError
          ? (isApiError(query.error)
            ? <CorrelationError error={query.error} />
            : <p className="text-[13px] text-destructive">{String(query.error)}</p>)
          : dashboard === undefined
            ? (
              <div className={kpiGridClass}>
                {Array.from({ length: 6 }, (_, i) => (
                  <Skeleton key={`kpi-skeleton-${i}`} className="h-[104px] rounded-lg" />
                ))}
              </div>
            )
            : <Glance dashboard={dashboard} />}
      </section>

      <HomeHero />

      {/* Each section loads on its own (a module may load it lazily), holding its place until it does. */}
      {moduleHomeSections().map(({ id, component: ModuleSection }) => (
        <Suspense key={id} fallback={<Skeleton className="h-48 rounded-lg" />}>
          <ModuleSection />
        </Suspense>
      ))}

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)]">
        <RecentRuns />
        <Card className="gap-0 overflow-hidden rounded-lg py-0" data-testid="runs-by-state-card">
          <div className="flex h-11 items-center border-b px-4">
            <h2 className="text-base font-medium">Runs by state</h2>
          </div>
          <div className="h-72 px-4 pt-4 pb-2">
            {dashboard === undefined ? (
              <Skeleton className="h-full w-full rounded-lg" />
            ) : noRuns ? (
              <EmptyState
                title="No runs recorded yet"
                description="Trigger a run and its state distribution appears here."
              />
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <BarChart {...chartBehaviour} data={runStates} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
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
                  <Tooltip cursor={<BandCursor fill={chartColors.cursor} />} content={RunStateTooltip} />
                  <Bar dataKey="count" name="Runs" radius={[4, 4, 0, 0]} maxBarSize={48} shape={RunStateBar} zIndex={chartLayers.bar} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </div>
        </Card>
      </div>
    </Page>
  );
}
