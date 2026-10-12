// The pure half of QueryResultView: what kind each column is, which chart form a result's shape fits, and the
// values that chart plots. Kept free of React and of the app's path aliases so the unit suite (gui/unit) can run it
// under Node directly.

/** One result column as the view reads it: its name as the query wrote it, and the provider's type name. */
export interface ResultColumn {
  name: string;
  dataType: string;
}

export type ColumnKind = "numeric" | "date" | "text";

/** Type-name allowlists, matched after stripping a length/precision suffix ("decimal(10,2)" -> "decimal") and
 * folding spacing, so the same check works across the provider families DataOps reaches (SQL Server, MySQL,
 * PostgreSQL, Oracle). */
const NUMERIC_TYPES = new Set([
  "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "float", "real", "money", "smallmoney",
  "double", "double precision", "integer", "serial", "bigserial", "smallserial", "number", "binary_float",
  "binary_double",
]);
const DATE_TYPES = new Set([
  "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "time", "timestamp",
  "timestamp with time zone", "timestamp without time zone",
]);

/** The provider type name reduced to what the allowlists hold. */
function bareType(dataType: string): string {
  return dataType.replace(/\([^)]*\)/g, "").replace(/\s+/g, " ").trim().toLowerCase();
}

export function classify(dataType: string): ColumnKind {
  const bare = bareType(dataType);
  if (NUMERIC_TYPES.has(bare)) return "numeric";
  if (DATE_TYPES.has(bare)) return "date";
  return "text";
}

/** A row value coerced to a finite number, or null when it is missing or not numeric text. Chart series skip
 * a null point rather than plotting a false zero. */
export function toNumber(value: string | null): number | null {
  if (value === null || value.trim() === "") return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** The key a column's values are plotted, keyed and tabled under. A query may name two columns alike, or leave
 * one unnamed (SQL Server returns "" for `SELECT COUNT(*), SUM(x)`), so a column is keyed by its position, never
 * by its name. */
export function columnKey(index: number): string {
  return `c${index}`;
}

/** What a column is called on screen: its name, or its position when the query gave it none. */
export function columnLabel(columns: readonly ResultColumn[], index: number): string {
  const name = columns[index]?.name ?? "";
  return name.trim().length > 0 ? name : `(column ${index + 1})`;
}

export type ChartPlan =
  | { form: "empty" }
  | { form: "stat"; numericIdx: number[] }
  | { form: "line"; dateIdx: number; numericIdx: number[] }
  | { form: "bar"; textIdx: number; numericIdx: number }
  | { form: "table" };

/** The form a result's shape fits, following the dataviz skill: a single headline is a stat tile, not a chart;
 * change over time is a line; magnitude by category is a bar; anything else is a table. A single row's stat tiles
 * show only its numbers, so a row that also carries text or dates is shown as a table instead of losing them. */
export function planChart(kinds: readonly ColumnKind[], rowCount: number): ChartPlan {
  if (rowCount === 0) {
    return { form: "empty" };
  }

  const numericIdx = kinds.flatMap((k, i) => (k === "numeric" ? [i] : []));
  const dateIdx = kinds.flatMap((k, i) => (k === "date" ? [i] : []));
  const textIdx = kinds.flatMap((k, i) => (k === "text" ? [i] : []));

  if (rowCount === 1 && numericIdx.length >= 1 && numericIdx.length <= 4 && numericIdx.length === kinds.length) {
    return { form: "stat", numericIdx };
  }

  if (dateIdx.length === 1 && numericIdx.length >= 1 && numericIdx.length <= 4 && rowCount > 1
    && textIdx.length === 0) {
    return { form: "line", dateIdx: dateIdx[0], numericIdx };
  }

  if (textIdx.length === 1 && numericIdx.length === 1 && dateIdx.length === 0 && rowCount > 1) {
    return { form: "bar", textIdx: textIdx[0], numericIdx: numericIdx[0] };
  }

  return { form: "table" };
}

const DATE_TIME = /^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?/;
const TIME_ONLY = /^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?/;

/**
 * The axis labels of a line chart's date column, one per value, as short as the series allows: a column whose
 * every value falls on midnight reads as its date alone (a `date` round-trips as a full ISO timestamp,
 * "2025-01-01T00:00:00.0000000"); one carrying a time of day keeps it, to the minute, or to the second when any value
 * has seconds; a `time` column reads as its time. A value the patterns do not recognise is shown as it is.
 */
export function axisLabels(values: readonly (string | null)[], dataType: string): string[] {
  if (bareType(dataType) === "time") {
    const anySeconds = values.some((v) => v !== null && Number(TIME_ONLY.exec(v)?.[3] ?? "0") !== 0);
    return values.map((v) => {
      const match = v === null ? null : TIME_ONLY.exec(v);
      if (match === null) return v ?? "";
      return anySeconds ? `${match[1]}:${match[2]}:${match[3] ?? "00"}` : `${match[1]}:${match[2]}`;
    });
  }

  const parsed = values.map((v) => (v === null ? null : DATE_TIME.exec(v)));
  const hasTimeOfDay = parsed.some((m) => m !== null && (Number(m[2] ?? "0") !== 0 || Number(m[3] ?? "0") !== 0
    || Number(m[4] ?? "0") !== 0 || Number(m[5] ?? "0") !== 0));
  const anySeconds = parsed.some((m) => m !== null && (Number(m[4] ?? "0") !== 0 || Number(m[5] ?? "0") !== 0));
  return values.map((v, i) => {
    const match = parsed[i];
    if (match === null || match === undefined) return v ?? "";
    if (!hasTimeOfDay) return match[1];
    const time = `${match[2] ?? "00"}:${match[3] ?? "00"}`;
    return anySeconds ? `${match[1]} ${time}:${match[4] ?? "00"}` : `${match[1]} ${time}`;
  });
}

/** One bar: its category's label and its value, null when the row holds no number for it (drawn as no bar, not as
 * a zero). */
export interface BarPoint {
  category: string;
  value: number | null;
}

/** The bars of a bar chart, the first `max` rows' categories and values. */
export function barData(
  rows: readonly (readonly (string | null)[])[], textIdx: number, numericIdx: number, max: number,
): BarPoint[] {
  return rows.slice(0, Math.max(0, max)).map((row) => ({
    category: row[textIdx] ?? "(null)",
    value: toNumber(row[numericIdx]),
  }));
}

/** The points of a line chart: ordered by the date column (nulls first), each with its axis label and every series'
 * value under its column key. */
export function linePoints(
  rows: readonly (readonly (string | null)[])[], dateIdx: number, dataType: string, numericIdx: readonly number[],
): Record<string, string | number | null>[] {
  const sorted = [...rows].sort((a, b) => (a[dateIdx] ?? "").localeCompare(b[dateIdx] ?? ""));
  const labels = axisLabels(sorted.map((row) => row[dateIdx]), dataType);
  return sorted.map((row, i) => {
    const point: Record<string, string | number | null> = { date: labels[i] };
    numericIdx.forEach((index) => { point[columnKey(index)] = toNumber(row[index]); });
    return point;
  });
}

/** How far apart the series' largest magnitudes are: the largest over the smallest of the positive ones, or 1 when
 * fewer than two series have any magnitude at all. Past a spread of five a shared axis flattens the smaller series,
 * so the view splits them into one chart each. */
export function scaleSpread(points: readonly Record<string, string | number | null>[], keys: readonly string[]): number {
  const maxima = keys.map((key) => Math.max(0, ...points.map((p) => Math.abs(Number(p[key]) || 0))));
  const positive = maxima.filter((m) => m > 0);
  return positive.length > 1 ? Math.max(...positive) / Math.min(...positive) : 1;
}
