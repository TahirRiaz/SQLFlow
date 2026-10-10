// Renders one ad-hoc query result (POWERAI.md's Run affordance) as a fitting chart plus a table, or a table
// alone when no chart form fits the shape. Follows the dataviz skill: the form is picked by the data's job
// (a single headline is a stat tile, not a chart; magnitude-by-category is a bar; change-over-time is a
// line), categorical hue is assigned in the app's own fixed order (--chart-1.. never cycled), and a table
// view always exists (dataviz check 6) rather than forcing a bad chart on a shape that does not fit one.

import { useMemo, useState } from "react";
import {
  Bar, BarChart, CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
  type TooltipProps,
} from "recharts";
import { Card } from "@/components/ui/card";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { KpiCard } from "@/components/KpiCard";
import { useThemeMode } from "@/theme/ThemeModeContext";
import type { QueryResultColumn } from "../../api/types";

interface QueryResultViewProps {
  columns: QueryResultColumn[];
  rows: (string | null)[][];
}

type ColumnKind = "numeric" | "date" | "text";

/** Type-name allowlists, matched after stripping a length/precision suffix ("decimal(10,2)" -> "decimal") so
 * the same check works across the provider families DataOps reaches (SQL Server, MySQL, PostgreSQL, Oracle). */
const NUMERIC_TYPES = new Set([
  "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "float", "real", "money", "smallmoney",
  "double", "double precision", "integer", "serial", "bigserial", "smallserial", "number", "binary_float",
  "binary_double",
]);
const DATE_TYPES = new Set([
  "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "time", "timestamp",
  "timestamp with time zone", "timestamp without time zone",
]);

function classify(dataType: string): ColumnKind {
  const bare = dataType.replace(/\(.*\)/, "").trim().toLowerCase();
  if (NUMERIC_TYPES.has(bare)) return "numeric";
  if (DATE_TYPES.has(bare)) return "date";
  return "text";
}

/** A row value coerced to a finite number, or null when it is missing or not numeric text. Chart series skip
 * a null point rather than plotting a false zero. */
function toNumber(value: string | null): number | null {
  if (value === null || value.trim() === "") return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** The categorical bar chart caps how many rows it plots (matching the app's own "top 10" convention in
 * Insights) so labels stay legible; the table underneath still carries every row. */
const MAX_CHART_CATEGORIES = 10;

const numberFormatter = new Intl.NumberFormat();

function formatValue(value: string | null): string {
  return value ?? "NULL";
}

/** A stat-tile value: a numeric string gets thousands separators and a sane decimal cap (a SUM/AVG
 * comes back as raw provider text, e.g. "148300.00", which is both harder to read and, at the KPI
 * card's font size, prone to wrapping mid-number); anything else (a null, a non-numeric aggregate)
 * falls back to its plain text. */
function formatStatValue(value: string | null): string {
  if (value === null) return "NULL";
  const n = Number(value);
  return Number.isFinite(n) ? numberFormatter.format(n) : value;
}

function ResultTooltip({ active, payload, label }: TooltipProps<number, string>) {
  if (active !== true || payload === undefined || payload.length === 0) {
    return null;
  }
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-mono font-medium">{label}</div>
      <div className="mt-1 flex flex-col gap-0.5 font-mono tabular-nums">
        {payload.map((entry) => (
          <span key={entry.dataKey as string} className="flex items-center gap-1.5">
            <span className="size-2 rounded-sm" style={{ background: entry.color }} aria-hidden />
            {entry.name}: {typeof entry.value === "number" ? numberFormatter.format(entry.value) : String(entry.value)}
          </span>
        ))}
      </div>
    </div>
  );
}

export function QueryResultView({ columns, rows }: QueryResultViewProps) {
  const { mode } = useThemeMode();
  const [view, setView] = useState<"chart" | "table">("chart");

  // The app's own fixed categorical order (index.css --chart-1..8), re-read on theme flips, matching
  // Insights/StreamDetailSheet's own convention rather than a palette private to this view.
  const ink = useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      series: [1, 2, 3, 4].map((n) => token(`--chart-${n}`)),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      cursor: token("--muted"),
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode]);

  const kinds = useMemo(() => columns.map((c) => classify(c.dataType)), [columns]);

  const plan = useMemo(() => {
    if (rows.length === 0) {
      return { form: "empty" as const };
    }

    const numericIdx = kinds.flatMap((k, i) => (k === "numeric" ? [i] : []));
    const dateIdx = kinds.flatMap((k, i) => (k === "date" ? [i] : []));
    const textIdx = kinds.flatMap((k, i) => (k === "text" ? [i] : []));

    // A single row is a headline, not a trend: one or a few numbers deserve stat tiles, per the dataviz
    // skill's "the answer is sometimes not a chart" guidance.
    if (rows.length === 1 && numericIdx.length >= 1 && numericIdx.length <= 4) {
      return { form: "stat" as const, numericIdx };
    }

    // Exactly one date/time column plus 1-4 numeric columns, multiple rows: change over time.
    if (dateIdx.length === 1 && numericIdx.length >= 1 && numericIdx.length <= 4 && rows.length > 1) {
      return { form: "line" as const, dateIdx: dateIdx[0], numericIdx };
    }

    // Exactly one text/categorical column plus exactly one numeric column: magnitude by category.
    if (textIdx.length === 1 && numericIdx.length === 1 && rows.length > 1) {
      return { form: "bar" as const, textIdx: textIdx[0], numericIdx: numericIdx[0] };
    }

    return { form: "table" as const };
  }, [rows, kinds]);

  // DataTable keys rows by a value derived from the row itself; a query result has no natural key, so each
  // row is tagged with its own position once, up front.
  const indexedRows = useMemo(() => rows.map((row, index) => ({ row, index })), [rows]);

  const tableColumns = useMemo<Column<{ row: (string | null)[]; index: number }>[]>(
    () => columns.map((c, i) => ({
      id: c.name,
      header: c.name,
      render: ({ row }) => (
        <span className={row[i] === null ? "text-muted-foreground italic" : "font-mono text-xs"}>
          {formatValue(row[i])}
        </span>
      ),
    })),
    [columns],
  );

  const table = (
    <DataTable
      columns={tableColumns}
      rows={indexedRows}
      rowKey={(r) => r.index}
      emptyMessage="No rows."
      data-testid="query-result-table"
    />
  );

  if (plan.form === "empty") {
    return <EmptyState title="No rows" description="The query ran but returned nothing to show." />;
  }

  if (plan.form === "stat") {
    return (
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4" data-testid="query-result-stat">
        {plan.numericIdx.map((i) => (
          <KpiCard key={columns[i].name} label={columns[i].name} value={formatStatValue(rows[0][i])} />
        ))}
      </div>
    );
  }

  if (plan.form === "table") {
    return table;
  }

  const toggle = (
    <ToggleGroup
      type="single"
      value={view}
      onValueChange={(value) => { if (value === "chart" || value === "table") setView(value); }}
      variant="outline"
      size="sm"
      data-testid="query-result-view-toggle"
    >
      <ToggleGroupItem value="chart" className="px-2.5 text-xs">Chart</ToggleGroupItem>
      <ToggleGroupItem value="table" className="px-2.5 text-xs">Table</ToggleGroupItem>
    </ToggleGroup>
  );

  if (view === "table") {
    return (
      <div className="flex flex-col gap-2">
        <div className="flex justify-end">{toggle}</div>
        {table}
      </div>
    );
  }

  if (plan.form === "bar") {
    const { textIdx, numericIdx } = plan;
    const data = rows.slice(0, MAX_CHART_CATEGORIES).map((row) => ({
      category: row[textIdx] ?? "(null)",
      value: toNumber(row[numericIdx]) ?? 0,
    }));
    const truncated = rows.length > MAX_CHART_CATEGORIES;
    return (
      <div className="flex flex-col gap-2">
        <div className="flex items-center justify-between">
          {truncated
            ? (
              <span className="text-xs text-muted-foreground">
                Showing the first {MAX_CHART_CATEGORIES} of {rows.length} rows; see the table for the rest.
              </span>
            )
            : <span />}
          {toggle}
        </div>
        <Card className="gap-0 rounded-lg p-3" style={{ height: Math.max(160, 40 + data.length * 32) }}>
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={data} layout="vertical" margin={{ top: 0, right: 16, bottom: 0, left: 8 }}>
              <CartesianGrid horizontal={false} stroke={ink.grid} />
              <XAxis
                type="number"
                stroke={ink.axis}
                tick={{ fill: ink.axis, fontSize: 12 }}
                tickFormatter={(value: number) => numberFormatter.format(value)}
                axisLine={{ stroke: ink.grid }}
                tickLine={false}
              />
              <YAxis
                type="category"
                dataKey="category"
                width={160}
                stroke={ink.axis}
                tick={{ fill: ink.axis, fontSize: 11 }}
                axisLine={{ stroke: ink.grid }}
                tickLine={false}
              />
              <Tooltip cursor={{ fill: ink.cursor }} content={<ResultTooltip />} />
              <Bar dataKey="value" name={columns[numericIdx].name} fill={ink.series[0]} radius={[0, 4, 4, 0]} maxBarSize={20} isAnimationActive={false} />
            </BarChart>
          </ResponsiveContainer>
        </Card>
        {truncated && table}
      </div>
    );
  }

  // plan.form === "line"
  const { dateIdx, numericIdx } = plan;
  const sorted = [...rows].sort((a, b) => (a[dateIdx] ?? "").localeCompare(b[dateIdx] ?? ""));
  const data = sorted.map((row) => {
    // Truncated to the date portion: a `date`/`smalldatetime` column round-trips through the query
    // result as a full ISO timestamp (".../...T00:00:00.0000000"), which is unreadable as an axis
    // tick or a tooltip label for a series that never carries a time-of-day component.
    const point: Record<string, string | number | null> = { date: (row[dateIdx] ?? "").slice(0, 10) };
    numericIdx.forEach((i) => { point[columns[i].name] = toNumber(row[i]); });
    return point;
  });

  // One axis, never two: dataviz's #1 rule. When two series' magnitudes are far apart (a revenue
  // total next to an order count, say), sharing an axis does not compromise readability, it destroys
  // it - the smaller series flattens to the axis floor. The skill's prescribed fix for "two measures
  // of different scale" is small multiples, not a second y-axis, so a wide spread splits into one
  // chart per series instead of one shared overlay.
  const seriesMax = numericIdx.map((i) => Math.max(0, ...data.map((p) => Math.abs(Number(p[columns[i].name]) || 0))));
  const positiveMax = seriesMax.filter((m) => m > 0);
  const scaleSpread = positiveMax.length > 1 ? Math.max(...positiveMax) / Math.min(...positiveMax) : 1;
  const asSmallMultiples = numericIdx.length > 1 && scaleSpread > 5;

  const renderOneLine = (seriesIdx: number, colorIndex: number, height: number, showAxisLabel: boolean) => (
    <Card key={columns[seriesIdx].name} className="gap-0 rounded-lg p-3" style={{ height }}>
      {showAxisLabel && (
        <div className="mb-1 flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <span className="size-2.5 rounded-sm" style={{ background: ink.series[colorIndex] }} aria-hidden />
          {columns[seriesIdx].name}
        </div>
      )}
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
          <CartesianGrid stroke={ink.grid} strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="date" stroke={ink.axis} tick={{ fontSize: 11 }} minTickGap={24} />
          <YAxis stroke={ink.axis} tick={{ fontSize: 11 }} width={52} tickFormatter={(value: number) => numberFormatter.format(value)} />
          <Tooltip content={<ResultTooltip />} cursor={{ stroke: ink.axis, strokeDasharray: "3 3" }} />
          <Line
            dataKey={columns[seriesIdx].name}
            name={columns[seriesIdx].name}
            stroke={ink.series[colorIndex]}
            strokeWidth={2}
            dot={data.length <= 30}
            isAnimationActive={false}
            connectNulls
          />
        </LineChart>
      </ResponsiveContainer>
    </Card>
  );

  if (asSmallMultiples) {
    return (
      <div className="flex flex-col gap-2">
        <div className="flex justify-end">{toggle}</div>
        <div className="flex flex-col gap-2">
          {numericIdx.map((i, seriesIndex) => renderOneLine(i, seriesIndex, 160, true))}
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        {numericIdx.length > 1
          ? (
            <div className="flex flex-wrap items-center gap-3 text-[11px] text-muted-foreground">
              {numericIdx.map((i, seriesIndex) => (
                <span key={columns[i].name} className="inline-flex items-center gap-1.5">
                  <span className="size-2.5 rounded-sm" style={{ background: ink.series[seriesIndex] }} aria-hidden />
                  {columns[i].name}
                </span>
              ))}
            </div>
          )
          : <span />}
        {toggle}
      </div>
      <Card className="gap-0 rounded-lg p-3" style={{ height: 220 }}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid stroke={ink.grid} strokeDasharray="3 3" vertical={false} />
            <XAxis dataKey="date" stroke={ink.axis} tick={{ fontSize: 11 }} minTickGap={24} />
            <YAxis stroke={ink.axis} tick={{ fontSize: 11 }} width={52} tickFormatter={(value: number) => numberFormatter.format(value)} />
            <Tooltip content={<ResultTooltip />} cursor={{ stroke: ink.axis, strokeDasharray: "3 3" }} />
            {numericIdx.map((i, seriesIndex) => (
              <Line
                key={columns[i].name}
                dataKey={columns[i].name}
                name={columns[i].name}
                stroke={ink.series[seriesIndex]}
                strokeWidth={2}
                dot={data.length <= 30}
                isAnimationActive={false}
                connectNulls
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </Card>
    </div>
  );
}
