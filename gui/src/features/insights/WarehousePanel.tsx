import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Play } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { datasourceApi } from "../../api/endpoints";
import type {
  ComputeOperation,
  ExpensiveQuery,
  IndexUsageEntry,
  IndexUsageResult,
  MissingIndexAdvisory,
  MissingIndexesResult,
  StatisticsAdvisory,
  StatisticsHealthResult,
  TopQueriesResult,
} from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { RelativeTime } from "../../components/RelativeTime";
import { formatDurationSeconds } from "../../lib/time";
import { useCompute } from "../datasources/useCompute";

type ProbeOperation = Extract<ComputeOperation, "missingIndexes" | "statisticsHealth" | "indexUsage" | "topQueries">;

const probes: Array<{ operation: ProbeOperation; label: string; description: string }> = [
  {
    operation: "missingIndexes",
    label: "Missing indexes",
    description: "Indexes the optimizer wanted for real workload, ranked by estimated improvement.",
  },
  {
    operation: "statisticsHealth",
    label: "Statistics",
    description: "Statistics freshness: rows modified since the last update, ranked most-stale first.",
  },
  {
    operation: "indexUsage",
    label: "Index usage",
    description: "Read/write balance per index; write-only indexes cost maintenance without serving queries.",
  },
  {
    operation: "topQueries",
    label: "Top queries",
    description: "The plan cache's most expensive statements by total elapsed time.",
  },
];

function formatMs(ms: number): string {
  return ms < 1000 ? `${ms.toFixed(0)} ms` : formatDurationSeconds(ms / 1000);
}

function mono(value: string | number): React.ReactNode {
  return <span className="font-mono text-xs tabular-nums">{value}</span>;
}

const missingIndexColumns: Column<MissingIndexAdvisory>[] = [
  { id: "table", header: "Table", render: (a) => mono(`${a.schema}.${a.table}`) },
  { id: "columns", header: "Key columns", render: (a) => mono(a.equalityColumns ?? a.inequalityColumns ?? "") },
  { id: "seeks", header: "Wanted", align: "right", render: (a) => mono((a.userSeeks + a.userScans).toLocaleString()) },
  { id: "impact", header: "Impact", align: "right", render: (a) => mono(`${a.avgUserImpactPercent.toFixed(0)}%`) },
  {
    id: "measure",
    header: "Score",
    align: "right",
    render: (a) => mono(Math.round(a.improvementMeasure).toLocaleString()),
  },
];

const statisticsColumns: Column<StatisticsAdvisory>[] = [
  { id: "table", header: "Table", render: (s) => mono(`${s.schema}.${s.table}`) },
  { id: "stat", header: "Statistic", render: (s) => mono(s.statisticName) },
  { id: "rows", header: "Rows", align: "right", render: (s) => mono(s.rows.toLocaleString()) },
  { id: "mods", header: "Modified", align: "right", render: (s) => mono(`${s.modificationPercent.toFixed(1)}%`) },
  {
    id: "updated",
    header: "Last updated",
    render: (s) => (s.lastUpdated === null ? mono("never") : <RelativeTime value={s.lastUpdated} />),
  },
  {
    id: "stale",
    header: "State",
    render: (s) =>
      s.isStale
        ? <span className="text-xs font-medium text-warning">stale</span>
        : <span className="text-xs text-muted-foreground">fresh</span>,
  },
];

const indexUsageColumns: Column<IndexUsageEntry>[] = [
  { id: "index", header: "Index", render: (i) => mono(i.indexName) },
  { id: "table", header: "Table", render: (i) => mono(`${i.schema}.${i.table}`) },
  { id: "reads", header: "Reads", align: "right", render: (i) => mono(i.reads.toLocaleString()) },
  { id: "writes", header: "Writes", align: "right", render: (i) => mono(i.writes.toLocaleString()) },
  { id: "size", header: "Size", align: "right", render: (i) => mono(`${Math.round(i.sizeKb / 1024).toLocaleString()} MB`) },
  {
    id: "verdict",
    header: "State",
    render: (i) =>
      i.isUnused
        ? <span className="text-xs font-medium text-warning">unused</span>
        : <span className="text-xs text-muted-foreground">{i.reads === 0 ? "idle" : "in use"}</span>,
  },
];

const topQueryColumns: Column<ExpensiveQuery>[] = [
  {
    id: "statement",
    header: "Statement",
    render: (q) => (
      <span className="block max-w-[420px] truncate font-mono text-xs" title={q.statementText}>
        {q.statementText}
      </span>
    ),
  },
  { id: "execs", header: "Execs", align: "right", render: (q) => mono(q.executionCount.toLocaleString()) },
  { id: "avg", header: "Avg", align: "right", render: (q) => mono(formatMs(q.avgElapsedMs)) },
  { id: "total", header: "Total", align: "right", render: (q) => mono(formatMs(q.totalElapsedMs)) },
  { id: "reads", header: "Avg reads", align: "right", render: (q) => mono(q.avgLogicalReads.toLocaleString()) },
  { id: "db", header: "Database", render: (q) => mono(q.database ?? "") },
];

/** The result shape each probe stores on its compute task, keyed by operation. */
interface ProbeResults {
  missingIndexes: MissingIndexesResult;
  statisticsHealth: StatisticsHealthResult;
  indexUsage: IndexUsageResult;
  topQueries: TopQueriesResult;
}

/**
 * One probe's surface: the newest stored result (compute tasks persist their results, so the panel is useful
 * without re-measuring), a run button that enqueues a fresh probe on a worker, and the operation's result
 * table. Selecting a row with a SQL suggestion (a CREATE INDEX, an UPDATE STATISTICS, a statement body) shows
 * it below the table for review and copy; nothing here executes SQL against the warehouse.
 */
function ProbeTab<TOp extends ProbeOperation>({ operation, description, reference }: {
  operation: TOp;
  description: string;
  reference: string;
}) {
  const queryClient = useQueryClient();
  const compute = useCompute<ProbeResults[TOp]>();
  const [selectedSql, setSelectedSql] = useState<string | null>(null);

  // The newest stored result for this probe against this datasource: the panel opens with data (and its age)
  // instead of an empty pane demanding a click.
  const lastTask = useQuery({
    queryKey: ["insights", "warehouse-task", operation, reference],
    queryFn: async () => {
      const page = await datasourceApi.tasks({ operation, reference, status: "succeeded", page: 1, pageSize: 1 });
      if (page.items.length === 0) {
        return null;
      }

      return datasourceApi.task(page.items[0].taskId);
    },
  });

  const result = compute.data ?? (lastTask.data?.result as ProbeResults[TOp] | undefined) ?? null;
  const measuredUtc = compute.data !== null ? null : lastTask.data?.endUtc ?? null;

  const run = async () => {
    setSelectedSql(null);
    const data = await compute.run({ reference, operation, limit: 100 });
    if (data !== null) {
      toast.success(`${probes.find((p) => p.operation === operation)?.label} analysis finished`);
      void queryClient.invalidateQueries({ queryKey: ["insights", "warehouse-task", operation, reference] });
      void queryClient.invalidateQueries({ queryKey: ["insights", "recommendations"] });
    }
  };

  const table = (() => {
    if (result === null) {
      return null;
    }

    switch (operation) {
      case "missingIndexes": {
        const advisories = (result as MissingIndexesResult).advisories;
        return (
          <DataTable
            columns={missingIndexColumns}
            rows={advisories}
            rowKey={(a) => `${a.schema}.${a.table}.${a.equalityColumns}.${a.inequalityColumns}`}
            onRowClick={(a) => setSelectedSql(a.suggestedIndexSql)}
            emptyMessage="No missing-index advisories: the optimizer is not asking for anything."
            data-testid="warehouse-missing-indexes"
          />
        );
      }

      case "statisticsHealth": {
        const statistics = (result as StatisticsHealthResult).statistics;
        return (
          <DataTable
            columns={statisticsColumns}
            rows={statistics}
            rowKey={(s) => `${s.schema}.${s.table}.${s.statisticName}`}
            onRowClick={(s) => setSelectedSql(s.suggestedUpdateSql)}
            emptyMessage="No statistics reported."
            data-testid="warehouse-statistics"
          />
        );
      }

      case "indexUsage": {
        const indexes = (result as IndexUsageResult).indexes;
        return (
          <DataTable
            columns={indexUsageColumns}
            rows={indexes}
            rowKey={(i) => `${i.schema}.${i.table}.${i.indexName}`}
            emptyMessage="No index usage reported."
            data-testid="warehouse-index-usage"
          />
        );
      }

      default: {
        const queries = (result as TopQueriesResult).queries;
        return (
          <DataTable
            columns={topQueryColumns}
            rows={queries}
            rowKey={(q) => `${q.cachedSince}.${q.statementText.slice(0, 80)}`}
            onRowClick={(q) => setSelectedSql(q.statementText)}
            emptyMessage="The plan cache reported no statements."
            data-testid="warehouse-top-queries"
          />
        );
      }
    }
  })();

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-[13px] text-muted-foreground">{description}</p>
        <div className="flex items-center gap-3">
          {measuredUtc !== null && (
            <span className="text-xs text-muted-foreground">
              Measured <RelativeTime value={measuredUtc} />
            </span>
          )}
          <Button size="sm" onClick={() => void run()} disabled={compute.running} data-testid={`warehouse-run-${operation}`}>
            {compute.running
              ? <Loader2 className="size-3.5 animate-spin" aria-hidden />
              : <Play className="size-3.5" aria-hidden />}
            {compute.running ? "Analyzing..." : "Analyze now"}
          </Button>
        </div>
      </div>
      {compute.error !== null && <p className="text-[13px] text-destructive">{compute.error}</p>}
      {result !== null
        ? table
        : lastTask.isLoading || compute.running
          ? <Skeleton className="h-40 rounded-lg" />
          : (
            <EmptyState
              title="Not measured yet"
              description="Run the analysis to probe the warehouse's management views from a worker node."
            />
          )}
      {selectedSql !== null && (
        <div className="flex flex-col gap-1">
          <div className="text-xs font-medium text-muted-foreground">Suggested SQL (review before running)</div>
          <CodeView value={selectedSql} language="sql" height={160} data-testid="warehouse-suggested-sql" />
        </div>
      )}
    </div>
  );
}

/**
 * The live-warehouse half of the insights page: DMV probes (missing indexes, statistics freshness, index
 * usage, expensive queries) executed on worker nodes through the compute-task rail against a datasource the
 * estate already declares. Defaults to the estate's busiest target (the DWH); only SQL Server family
 * references qualify because the probes are T-SQL.
 */
export function WarehousePanel() {
  const [reference, setReference] = useState<string | null>(null);
  const datasources = useQuery({ queryKey: ["datasources", "for-insights"], queryFn: datasourceApi.list });

  const candidates = useMemo(
    () =>
      (datasources.data ?? [])
        .filter((d) => d.resolvable && (d.kind === null || d.kind === "MSSQL" || d.kind === "AZDB"))
        .sort((a, b) => b.targetPipelines - a.targetPipelines),
    [datasources.data],
  );
  // The busiest target reference IS the warehouse in this estate's model; it stays the default while the
  // select lets an operator probe any other SQL Server datasource the flows declare.
  const selected = reference ?? candidates[0]?.reference ?? null;

  return (
    <Card className="gap-0 rounded-lg py-4" data-testid="warehouse-health-card">
      <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-2 px-4">
        <CardTitle className="text-base font-medium">Warehouse health</CardTitle>
        {candidates.length > 1 && selected !== null && (
          <Select value={selected} onValueChange={setReference}>
            <SelectTrigger className="h-8 w-72 font-mono text-xs" data-testid="warehouse-reference-select">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {candidates.map((candidate) => (
                <SelectItem key={candidate.reference} value={candidate.reference} className="font-mono text-xs">
                  {candidate.reference}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </CardHeader>
      <CardContent className="px-4 pt-3">
        {datasources.isLoading ? (
          <Skeleton className="h-40 rounded-lg" />
        ) : selected === null ? (
          <EmptyState
            title="No SQL Server datasource to probe"
            description="The DMV probes need a resolvable SQL Server / Azure SQL connection reference declared by the estate's pipelines."
          />
        ) : (
          <Tabs defaultValue="missingIndexes">
            <TabsList>
              {probes.map((probe) => (
                <TabsTrigger key={probe.operation} value={probe.operation} data-testid={`warehouse-tab-${probe.operation}`}>
                  {probe.label}
                </TabsTrigger>
              ))}
            </TabsList>
            {probes.map((probe) => (
              <TabsContent key={probe.operation} value={probe.operation} className="pt-2">
                <ProbeTab operation={probe.operation} description={probe.description} reference={selected} />
              </TabsContent>
            ))}
          </Tabs>
        )}
      </CardContent>
    </Card>
  );
}
