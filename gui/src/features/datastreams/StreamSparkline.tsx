import type { StreamSparkline as Sparkline } from "../../api/types";
import { RichTooltip } from "../../components/RichTooltip";
import { formatRows } from "./streamPresentation";

interface StreamSparklineProps {
  sparkline: Sparkline;
  width?: number;
  height?: number;
}

const DAY_MS = 86_400_000;

/** The calendar day `offset` days after the sparkline's first day, as MM-DD. The wire date is a UTC midnight
 * without a zone suffix, so it is rebuilt from its parts rather than parsed as local time, which on a
 * positive-offset laptop would shift every label back a day. */
function dayLabel(fromUtc: string, offset: number): string {
  const [year, month, day] = fromUtc.slice(0, 10).split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day) + offset * DAY_MS).toISOString().slice(5, 10);
}

/**
 * A stream's last two weeks in the width of a table cell: one bar per day for what arrived, the dashed line
 * for what the model expected, so "less data than usual" is a shape a reader sees before reading a word.
 * Inline SVG rather than a chart library: a board draws hundreds of these, and each is fourteen rectangles.
 *
 * Ink follows the chart rules (DESIGN.md 3.4): the one series in slot 1, the expectation in recessive text ink
 * because a reference is not a second series, and the days the analysis flagged in the reserved destructive
 * color. An expected day that wrote nothing has no bar to color, so it gets a stub at the baseline in the
 * warning color, which is the color of the "missing data" verdict it feeds. The trailing days still arriving
 * are drawn faint, matching the detail chart. Nothing here rests on color alone: the hover panel names every
 * day with its numbers and its verdict.
 */
export function StreamSparkline({ sparkline, width = 140, height = 26 }: StreamSparklineProps) {
  const count = sparkline.rows.length;
  if (count === 0 || sparkline.fromUtc === null) {
    return <span className="text-[11px] text-muted-foreground">no analysed days</span>;
  }

  const gap = 2;
  const slot = (width - gap * (count - 1)) / count;
  const flagged = new Set(sparkline.flaggedDays);
  const missed = new Set(sparkline.missedDays);
  const firstImmature = count - sparkline.immatureDays;

  // The scale tops out at three times the largest expectation, so one catch-up load of a hundred times an
  // ordinary day cannot flatten the thirteen ordinary days beside it into a row of one-pixel stubs. A bar
  // above that ceiling fills the cell; its true size is in the panel.
  const largestExpected = Math.max(0, ...sparkline.expected);
  const largestRow = Math.max(0, ...sparkline.rows);
  const peak = Math.max(1, largestExpected > 0 ? Math.min(largestRow, 3 * largestExpected) : largestRow);
  const usable = height - 1;
  const yOf = (value: number) => height - (Math.min(value, peak) / peak) * usable;
  const xOf = (index: number) => index * (slot + gap);

  const expectedPath = sparkline.expected
    .map((value, i) => `${i === 0 ? "M" : "L"}${(xOf(i) + slot / 2).toFixed(1)},${yOf(value).toFixed(1)}`)
    .join(" ");

  const lines = sparkline.rows.map((rows, i) => {
    const verdict = i >= firstImmature ? "arriving" : flagged.has(i) ? "flagged" : missed.has(i) ? "missed" : "";
    return `${dayLabel(sparkline.fromUtc!, i)}  ${formatRows(rows).padStart(11)} written` +
      `  ${formatRows(sparkline.expected[i]).padStart(11)} expected${verdict === "" ? "" : `  ${verdict}`}`;
  });

  const judged = sparkline.rows.slice(0, firstImmature);
  const total = judged.reduce((sum, rows) => sum + rows, 0);
  const summary = `Last ${count} days: ${formatRows(total)} rows written over ${judged.length} judged day(s), ` +
    `${sparkline.flaggedDays.length} flagged, ${sparkline.missedDays.length} expected day(s) with nothing.`;

  return (
    <RichTooltip title={`Last ${count} days`} body={lines.join("\n")} mono>
      <svg
        width={width}
        height={height}
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={summary}
        className="block shrink-0 overflow-visible"
      >
        {sparkline.rows.map((rows, i) => {
          const x = xOf(i);
          const faint = i >= firstImmature;
          if (rows <= 0) {
            return missed.has(i)
              ? <rect key={i} x={x} y={height - 2} width={slot} height={2} fill="var(--warning)" opacity={faint ? 0.45 : 1} />
              : <rect key={i} x={x} y={height - 1} width={slot} height={1} fill="var(--border)" />;
          }

          const barHeight = Math.max(1, height - yOf(rows));
          return (
            <rect
              key={i}
              x={x}
              y={height - barHeight}
              width={slot}
              height={barHeight}
              rx={1}
              fill={flagged.has(i) ? "var(--destructive)" : "var(--chart-1)"}
              opacity={faint ? 0.45 : 1}
            />
          );
        })}
        {largestExpected > 0 && (
          <path
            d={expectedPath}
            fill="none"
            stroke="var(--muted-foreground)"
            strokeWidth={1}
            strokeDasharray="2 2"
            vectorEffect="non-scaling-stroke"
          />
        )}
      </svg>
    </RichTooltip>
  );
}
