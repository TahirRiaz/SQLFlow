import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, Sparkles, TrendingDown, TrendingUp } from "lucide-react";
import { useNavigate } from "react-router-dom";
import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipProps,
} from "recharts";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { isApiError } from "../../api/client";
import { insightsApi } from "../../api/endpoints";
import type { FlowInsight, Recommendation } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CopyButton } from "../../components/CopyButton";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { RunStatusBadge } from "../../components/StatusBadge";
import { pollingInterval } from "../../hooks/usePolling";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { formatDurationSeconds } from "../../lib/time";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { SeverityBadge } from "./SeverityBadge";
import { StepsSheet } from "./StepsSheet";
import { WarehousePanel } from "./WarehousePanel";

const kpiGridClass = "grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-4";

const windowChoices = [
  { days: 1, label: "24h" },
  { days: 7, label: "7d" },
  { days: 30, label: "30d" },
];

/** The time-consumption chart's hover readout, on the popover surface like every floating layer. */
function TimeShareTooltip({ active, payload }: TooltipProps<number, string>) {
  if (active !== true || payload === undefined || payload.length === 0) {
    return null;
  }

  const flow = payload[0].payload as FlowInsight;
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-mono font-medium">{flow.flowName}</div>
      <div className="mt-0.5 font-mono tabular-nums">
        {formatDurationSeconds(flow.totalDurationSeconds)} across {flow.runs} run(s)
      </div>
    </div>
  );
}

/** One advisory row: severity + sentence, the flow it concerns as a link, and its suggested SQL on demand. */
function RecommendationRow({ item }: { item: Recommendation }) {
  const navigate = useNavigate();
  const [showSql, setShowSql] = useState(false);

  return (
    <div className="flex flex-col gap-1.5 border-b border-border/60 py-2.5 last:border-b-0">
      <div className="flex flex-wrap items-center gap-2">
        <SeverityBadge severity={item.severity} />
        {item.pipelineId !== null ? (
          <button
            className="text-left text-[13px] font-medium hover:underline"
            onClick={() => navigate(`/pipelines/${item.pipelineId}`)}
          >
            {item.title}
          </button>
        ) : (
          <span className="text-[13px] font-medium">{item.title}</span>
        )}
        {item.source === "warehouseDmv" && (
          <span className="rounded-sm bg-muted px-1.5 py-0.5 text-[11px] text-muted-foreground">warehouse</span>
        )}
      </div>
      <p className="text-[13px] leading-5 text-muted-foreground">{item.detail}</p>
      {item.suggestedSql !== null && (
        <div className="flex flex-col gap-1">
          <div className="flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              className="h-6 gap-1 px-1.5 text-xs text-muted-foreground"
              onClick={() => setShowSql((current) => !current)}
            >
              {showSql ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
              Suggested SQL
            </Button>
            <CopyButton label="Copy SQL" text={item.suggestedSql} testId="recommendation-copy-sql" iconOnly />
          </div>
          {showSql && <CodeView value={item.suggestedSql} language="sql" height={140} />}
        </div>
      )}
    </div>
  );
}

/**
 * The optimization dashboard (DESIGN.md 7.7): where the estate's processing time goes, what needs attention
 * (repeat failures, degradations, zero-row loads, silent flows, warehouse advisories), and the per-flow
 * performance table with step-level drill-down. Everything here is computed from telemetry the product already
 * collects; the warehouse panel below adds live DMV probes executed on worker nodes.
 */
export default function InsightsPage() {
  const { mode } = useThemeMode();
  const navigate = useNavigate();
  const [days, setDays] = useLocalStorageState<number>("insights.windowDays", 7);
  const [drill, setDrill] = useState<{ pipelineId: string; flowName: string } | null>(null);
  const [showAllAttention, setShowAllAttention] = useState(false);
  const [showAllFlows, setShowAllFlows] = useState(false);

  const flowsQuery = useQuery({
    queryKey: ["insights", "flows", days],
    queryFn: () => insightsApi.flows({ days, limit: 200 }),
    refetchInterval: pollingInterval(60000),
  });
  // The GUI reads the full form (SQL inline, deep list) and discloses progressively; the compact default
  // exists for context-limited clients like the MCP tools.
  const recommendationsQuery = useQuery({
    queryKey: ["insights", "recommendations", days],
    queryFn: () => insightsApi.recommendations({ days, limit: 100, includeSql: true }),
    refetchInterval: pollingInterval(60000),
  });

  // Chart ink comes off the same custom properties as the rest of the app (DESIGN.md 3.4): one series, one
  // categorical slot; the muted axis/grid tokens for the recessive anatomy. Re-read when the theme flips.
  const chartColors = useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return { series: token("--chart-1"), axis: token("--muted-foreground"), grid: token("--border"), cursor: token("--muted") };
  }, [mode]);

  const flowColumns = useMemo<Column<FlowInsight>[]>(() => [
    {
      id: "flow",
      header: "Flow",
      render: (f) => (
        <div className="flex flex-col">
          <span className="font-mono text-xs">{f.flowName}</span>
          {f.batch !== null && <span className="text-[11px] text-muted-foreground">{f.batch}</span>}
        </div>
      ),
    },
    { id: "kind", header: "Kind", render: (f) => <span className="font-mono text-xs">{f.flowKind}</span> },
    {
      id: "runs",
      header: "Runs",
      align: "right",
      render: (f) => (
        <span className="font-mono text-xs tabular-nums">
          {f.runs}
          {f.failures > 0 && <span className="text-destructive"> ({f.failures} failed)</span>}
        </span>
      ),
    },
    {
      id: "avg",
      header: "Avg",
      align: "right",
      render: (f) => (
        <span className="font-mono text-xs tabular-nums">
          {f.avgDurationSeconds === null ? "-" : formatDurationSeconds(f.avgDurationSeconds)}
        </span>
      ),
    },
    {
      id: "trend",
      header: "Trend",
      align: "right",
      render: (f) => {
        if (f.durationTrendPercent === null) {
          return <span className="text-xs text-muted-foreground">-</span>;
        }

        const slower = f.durationTrendPercent >= 0;
        // Trend is a state, so it wears status ink (slower = warning, faster = success), icon + signed number.
        return (
          <span className={`inline-flex items-center gap-1 font-mono text-xs tabular-nums ${slower ? "text-warning" : "text-success"}`}>
            {slower ? <TrendingUp className="size-3.5" aria-hidden /> : <TrendingDown className="size-3.5" aria-hidden />}
            {slower ? "+" : ""}{f.durationTrendPercent.toFixed(0)}%
          </span>
        );
      },
    },
    {
      id: "total",
      header: "Total",
      align: "right",
      render: (f) => <span className="font-mono text-xs tabular-nums">{formatDurationSeconds(f.totalDurationSeconds)}</span>,
    },
    {
      id: "throughput",
      header: "Rows/s",
      align: "right",
      render: (f) => (
        <span className="font-mono text-xs tabular-nums">
          {f.rowsPerSecond === null || f.rowsPerSecond === 0 ? "-" : Math.round(f.rowsPerSecond).toLocaleString()}
        </span>
      ),
    },
    {
      id: "last",
      header: "Last run",
      render: (f) => (
        <div className="flex items-center gap-2">
          <RunStatusBadge status={f.lastStatus} />
          <RelativeTime value={f.lastRunUtc} />
        </div>
      ),
    },
  ], []);

  if (flowsQuery.isError) {
    return (
      <Page data-testid="page-insights">
        <PageHeader title="Insights" />
        {isApiError(flowsQuery.error)
          ? <CorrelationError error={flowsQuery.error} />
          : <p className="text-[13px] text-destructive">{String(flowsQuery.error)}</p>}
      </Page>
    );
  }

  const flows = flowsQuery.data;
  const recommendations = recommendationsQuery.data;
  const windowToggle = (
    <ToggleGroup
      type="single"
      value={String(days)}
      onValueChange={(value) => {
        if (value !== "") {
          setDays(Number(value));
        }
      }}
      variant="outline"
      size="sm"
      data-testid="insights-window-toggle"
    >
      {windowChoices.map((choice) => (
        <ToggleGroupItem key={choice.days} value={String(choice.days)} className="px-2.5 text-xs">
          {choice.label}
        </ToggleGroupItem>
      ))}
    </ToggleGroup>
  );

  if (flows === undefined) {
    return (
      <Page data-testid="page-insights">
        <PageHeader title="Insights" actions={windowToggle} />
        <div className={kpiGridClass}>
          {Array.from({ length: 5 }, (_, i) => (
            <Skeleton key={`kpi-skeleton-${i}`} className="h-[104px] rounded-lg" />
          ))}
        </div>
        <Skeleton className="h-64 rounded-lg" />
        <Skeleton className="h-96 rounded-lg" />
      </Page>
    );
  }

  const failurePercent = flows.totalRuns > 0 ? (flows.totalFailures / flows.totalRuns) * 100 : 0;
  const criticalCount = recommendations?.criticalCount ?? 0;
  const warningCount = recommendations?.warningCount ?? 0;
  const topByTime = flows.flows.slice(0, 10);
  const attentionItems = recommendations?.items ?? [];
  const visibleAttention = showAllAttention ? attentionItems : attentionItems.slice(0, 6);
  const visibleFlows = showAllFlows ? flows.flows : flows.flows.slice(0, 10);

  return (
    <Page data-testid="page-insights">
      <PageHeader
        title="Insights"
        subtitle={`Last ${days === 1 ? "24 hours" : `${days} days`} of run telemetry and warehouse health`}
        actions={windowToggle}
      />

      <div className={kpiGridClass}>
        <KpiCard
          label="Processing time"
          value={formatDurationSeconds(flows.totalDurationSeconds)}
          caption={`across ${flows.totalRuns} runs`}
          testId="kpi-processing-time"
        />
        <KpiCard
          label="Failure rate"
          value={`${failurePercent.toFixed(failurePercent > 0 && failurePercent < 1 ? 1 : 0)}%`}
          caption={`${flows.totalFailures} failed runs`}
          color={flows.totalFailures > 0 ? "error" : "success"}
          testId="kpi-failure-rate"
        />
        <KpiCard
          label="Rows loaded"
          value={flows.totalRowsLoaded.toLocaleString()}
          testId="kpi-rows-loaded"
        />
        <KpiCard
          label="Needs attention"
          value={criticalCount + warningCount}
          caption={`${criticalCount} critical, ${warningCount} warnings`}
          color={criticalCount > 0 ? "error" : warningCount > 0 ? "warning" : "success"}
          testId="kpi-attention"
        />
        <KpiCard
          label="Flows measured"
          value={flows.flows.length}
          caption="with runs in the window"
          testId="kpi-flows-measured"
        />
      </div>

      <Card className="gap-0 rounded-lg py-4" data-testid="insights-attention-card">
        <CardHeader className="px-4">
          <CardTitle className="text-base font-medium">What to focus on</CardTitle>
        </CardHeader>
        <CardContent className="px-4">
          {recommendationsQuery.isError ? (
            isApiError(recommendationsQuery.error)
              ? <CorrelationError error={recommendationsQuery.error} />
              : <p className="text-[13px] text-destructive">{String(recommendationsQuery.error)}</p>
          ) : recommendations === undefined ? (
            <Skeleton className="h-32 rounded-lg" />
          ) : recommendations.items.length === 0 ? (
            <EmptyState
              icon={<Sparkles aria-hidden />}
              title="Nothing needs attention"
              description="Every flow ran clean over the window and no warehouse advisories are outstanding."
            />
          ) : (
            <div className="flex flex-col" data-testid="insights-attention-list">
              {visibleAttention.map((item, index) => (
                <RecommendationRow key={`${item.category}-${item.pipelineId ?? item.title}-${index}`} item={item} />
              ))}
              {attentionItems.length > 6 && (
                <div className="pt-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 px-2 text-xs text-muted-foreground"
                    onClick={() => setShowAllAttention((current) => !current)}
                    data-testid="insights-attention-toggle"
                  >
                    {showAllAttention
                      ? "Show top 6"
                      : `Show all ${recommendations.totalItems > attentionItems.length
                          ? `${attentionItems.length} of ${recommendations.totalItems}`
                          : attentionItems.length}`}
                  </Button>
                </div>
              )}
            </div>
          )}
        </CardContent>
      </Card>

      <Card className="gap-0 rounded-lg py-4" data-testid="insights-time-chart-card">
        <CardHeader className="px-4">
          <CardTitle className="text-base font-medium">Where the time goes</CardTitle>
        </CardHeader>
        <CardContent className="px-4 pt-3" style={{ height: Math.max(160, 40 + topByTime.length * 32) }}>
          {topByTime.length === 0 ? (
            <EmptyState
              title="No terminal runs in the window"
              description="Once flows run, their processing-time distribution appears here."
            />
          ) : (
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={topByTime} layout="vertical" margin={{ top: 0, right: 16, bottom: 0, left: 8 }}>
                <CartesianGrid horizontal={false} stroke={chartColors.grid} />
                <XAxis
                  type="number"
                  stroke={chartColors.axis}
                  tick={{ fill: chartColors.axis, fontSize: 12 }}
                  tickFormatter={(value: number) => formatDurationSeconds(value)}
                  axisLine={{ stroke: chartColors.grid }}
                  tickLine={false}
                />
                <YAxis
                  type="category"
                  dataKey="flowName"
                  width={220}
                  stroke={chartColors.axis}
                  tick={{ fill: chartColors.axis, fontSize: 11, fontFamily: "var(--font-mono, monospace)" }}
                  axisLine={{ stroke: chartColors.grid }}
                  tickLine={false}
                />
                <Tooltip cursor={{ fill: chartColors.cursor }} content={<TimeShareTooltip />} />
                <Bar
                  dataKey="totalDurationSeconds"
                  name="Processing time"
                  fill={chartColors.series}
                  radius={[0, 4, 4, 0]}
                  maxBarSize={20}
                />
              </BarChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>

      <Card className="gap-0 rounded-lg py-4" data-testid="insights-flows-card">
        <CardHeader className="flex flex-row items-center justify-between px-4">
          <CardTitle className="text-base font-medium">Flow performance</CardTitle>
          <Button variant="outline" size="sm" onClick={() => navigate("/runs")} data-testid="insights-open-runs">
            Open runs
          </Button>
        </CardHeader>
        <CardContent className="px-4 pt-3">
          <DataTable
            columns={flowColumns}
            rows={visibleFlows}
            rowKey={(f) => f.pipelineId}
            onRowClick={(f) => setDrill({ pipelineId: f.pipelineId, flowName: f.flowName })}
            emptyMessage="No terminal runs in the window."
            data-testid="insights-flows-table"
            footer={flows.flows.length > 10 ? (
              <div className="px-2 py-1.5">
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-7 px-2 text-xs text-muted-foreground"
                  onClick={() => setShowAllFlows((current) => !current)}
                  data-testid="insights-flows-toggle"
                >
                  {showAllFlows ? "Show top 10" : `Show all ${flows.flows.length}`}
                </Button>
              </div>
            ) : undefined}
          />
        </CardContent>
      </Card>

      <WarehousePanel />

      <StepsSheet
        pipelineId={drill?.pipelineId ?? null}
        flowName={drill?.flowName ?? null}
        windowDays={Math.max(days, 7)}
        onClose={() => setDrill(null)}
      />
    </Page>
  );
}
