import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import type { RunStatus } from "../../api/types";
import { brandToken } from "../../theme/branding";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { formatDurationSeconds } from "../../lib/time";
import {
  chosenView, FOCUS_DAYS, resolveView, statusTone, type QuietWindow, type RunBar, type TimelineRow, type TimelineView,
} from "./timeline";

/** The design tokens the SVG draws with, read off the document via getComputedStyle (the SVG attributes need
 * resolved color strings). Cached per design and mode; a change re-reads them. Never hex literals. */
interface ChartPalette {
  success: string;
  destructive: string;
  info: string;
  warning: string;
  muted: string;
  primary: string;
  border: string;
  foreground: string;
  card: string;
}

function readPalette(): ChartPalette {
  return {
    success: brandToken("--success"),
    destructive: brandToken("--destructive"),
    info: brandToken("--info"),
    warning: brandToken("--warning"),
    muted: brandToken("--muted-foreground"),
    primary: brandToken("--primary"),
    border: brandToken("--border"),
    foreground: brandToken("--foreground"),
    card: brandToken("--card"),
  };
}

interface ScheduleTimelineChartProps {
  rows: TimelineRow[];
  /** The fetched window: the navigator's full extent. The chart opens on its latest FOCUS_DAYS. A new range is a new
   * chart (the page keys it by range), while a refetch of the same range slides the window and keeps the view. */
  windowStartMs: number;
  windowEndMs: number;
  nowMs: number;
  /** The stretch of the day with nothing executing, shaded on every day in view; null when there is none. */
  freeWindow: QuietWindow | null;
}

const LABEL_W = 208;
const AXIS_H = 30;
const LANE_H = 30;
const BAR_H = 14;
const MIN_BAR_W = 3;
const MIN_SPAN_MS = 15 * 60 * 1000;
const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;
const FOCUS_MAX_H = 440;
const NAV_H = 58;
const NAV_LANE_TOP = 20;
const NAV_LANE_BOTTOM = 44;
const HANDLE_HIT = 10;

interface HoverState {
  bar: RunBar;
  x: number;
  y: number;
}

interface Tick {
  ms: number;
  label: string;
  emphasized: boolean;
}

type NavMode = "pan" | "left" | "right";

function pad2(value: number): string {
  return value.toString().padStart(2, "0");
}

function localMidnight(ms: number): number {
  const d = new Date(ms);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

const weekdayDay = new Intl.DateTimeFormat(undefined, { weekday: "short", day: "2-digit" });
const dateTimeLabel = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

/** The tick step for a visible span: hourly when drilled into part of a day, coarsening to two-day marks across
 * the widest windows, always aligned so day boundaries land on a tick. */
function tickStep(span: number): number {
  if (span <= 6 * HOUR_MS) {
    return HOUR_MS;
  }

  if (span <= 2 * DAY_MS) {
    return 3 * HOUR_MS;
  }

  return span <= 16 * DAY_MS ? DAY_MS : 2 * DAY_MS;
}

/** Axis ticks aligned to local day boundaries, at a density that suits the visible span. */
function makeTicks(startMs: number, endMs: number): Tick[] {
  const stepMs = tickStep(endMs - startMs);
  const ticks: Tick[] = [];
  for (let ms = localMidnight(startMs); ms <= endMs; ms += stepMs) {
    if (ms < startMs) {
      continue;
    }

    const d = new Date(ms);
    const atMidnight = d.getHours() === 0;
    const label = stepMs < DAY_MS && !atMidnight ? `${pad2(d.getHours())}:00` : weekdayDay.format(d);
    ticks.push({ ms, label, emphasized: atMidnight });
    if (ticks.length > 400) {
      break;
    }
  }

  return ticks;
}

/** One of the navigator's two view choices: a quiet text button, or the plain words of the one in effect. */
function ViewChoice({ current, onClick, testId, children }: {
  current: boolean;
  onClick: () => void;
  testId: string;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={current}
      aria-pressed={current}
      className={cn(
        "rounded-sm underline-offset-4 outline-none focus-visible:ring-1 focus-visible:ring-ring",
        current ? "font-medium text-foreground" : "text-primary hover:underline",
      )}
      data-testid={testId}
    >
      {children}
    </button>
  );
}

/** A Gantt-style availability chart: one lane per schedule, one bar per run, colored by run status, with the day's free
 * window shaded behind the lanes. The focus chart on top opens on the latest FOCUS_DAYS; the navigator strip below is a
 * brush over the whole fetched range, drag its body to pan and its edges to zoom (the focus chart also takes
 * scroll-to-zoom and drag-to-pan), and its gutter switches between the latest days and the whole window. */
export function ScheduleTimelineChart({ rows, windowStartMs, windowEndMs, nowMs, freeWindow }: ScheduleTimelineChartProps) {
  const { palette: tokenKey } = useThemeMode();
  // Token values change with the design and mode put on <html>; the palette key keys the cache so a change re-reads
  // them (getComputedStyle has no reactivity of its own).
  // eslint-disable-next-line react-hooks/exhaustive-deps -- tokenKey is the cache key itself, never read inside.
  const palette = useMemo(() => readPalette(), [tokenKey]);
  const containerRef = useRef<HTMLDivElement>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const navRef = useRef<SVGSVGElement>(null);
  const [width, setWidth] = useState(0);
  const [chosen, setChosen] = useState<TimelineView>({ kind: "latest" });
  const [hover, setHover] = useState<HoverState | null>(null);
  const [hoverDay, setHoverDay] = useState<number | null>(null);
  const dragRef = useRef<{ startX: number; viewStart: number; viewEnd: number } | null>(null);
  const navDragRef = useRef<{ mode: NavMode; startX: number; viewStart: number; viewEnd: number } | null>(null);

  const statusColor = useCallback(
    (status: RunStatus | null): string => palette[statusTone(status)],
    [palette],
  );

  const flatBars = useMemo(() => rows.flatMap((row) => row.bars), [rows]);
  // The view is derived from what was chosen, not stored as two instants, so the 30-second refetch that slides the
  // window to now keeps an operator's zoom where they put it (and the latest view on the newest runs).
  const view = useMemo(
    () => resolveView(chosen, flatBars, windowStartMs, windowEndMs),
    [chosen, flatBars, windowStartMs, windowEndMs],
  );
  const settle = useCallback(
    ([start, end]: [number, number]) => setChosen(chosenView(start, end, windowEndMs)),
    [windowEndMs],
  );

  useLayoutEffect(() => {
    const el = containerRef.current;
    if (el === null) {
      return undefined;
    }

    setWidth(el.clientWidth);
    const observer = new ResizeObserver((entries) => setWidth(entries[0].contentRect.width));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const plotW = Math.max(1, width - LABEL_W);
  const [viewStart, viewEnd] = view;
  const viewSpan = viewEnd - viewStart;
  const windowSpan = Math.max(1, windowEndMs - windowStartMs);

  const xOf = useCallback(
    (ms: number) => LABEL_W + ((ms - viewStart) / viewSpan) * plotW,
    [viewStart, viewSpan, plotW],
  );

  const navXOf = useCallback(
    (ms: number) => LABEL_W + ((ms - windowStartMs) / windowSpan) * plotW,
    [windowStartMs, windowSpan, plotW],
  );

  // Keeps a viewport span fixed while sliding its start inside the window (pan).
  const panTo = useCallback(
    (desiredStart: number, span: number): [number, number] => {
      const clampedSpan = Math.min(span, windowSpan);
      const start = Math.max(windowStartMs, Math.min(desiredStart, windowEndMs - clampedSpan));
      return [start, start + clampedSpan];
    },
    [windowStartMs, windowEndMs, windowSpan],
  );

  // Sets both edges explicitly, kept within the window and never narrower than the minimum span (resize/zoom).
  const resizeTo = useCallback(
    (start: number, end: number): [number, number] => {
      const s = Math.max(windowStartMs, Math.min(start, windowEndMs - MIN_SPAN_MS));
      const e = Math.min(windowEndMs, Math.max(end, s + MIN_SPAN_MS));
      return [s, e];
    },
    [windowStartMs, windowEndMs],
  );

  // Drill into the single local day that contains an instant: the "what ran on this day" view. Clamped to the
  // fetched window, so drilling into the first or last partial day still shows the part that exists.
  const zoomToDay = useCallback(
    (ms: number) => {
      const dayStart = Math.max(windowStartMs, localMidnight(ms));
      const dayEnd = Math.min(windowEndMs, localMidnight(ms) + DAY_MS);
      if (dayEnd - dayStart >= MIN_SPAN_MS) {
        settle([dayStart, dayEnd]);
      }
    },
    [windowStartMs, windowEndMs, settle],
  );

  // Wheel-zoom on the focus chart is a non-passive native listener so it can preventDefault the page scroll.
  useEffect(() => {
    const svg = svgRef.current;
    if (svg === null) {
      return undefined;
    }

    const onWheel = (event: WheelEvent) => {
      const rect = svg.getBoundingClientRect();
      const mouseX = event.clientX - rect.left;
      if (mouseX < LABEL_W) {
        return;
      }

      event.preventDefault();
      const cursorMs = viewStart + ((mouseX - LABEL_W) / plotW) * viewSpan;
      const factor = event.deltaY < 0 ? 1 / 1.2 : 1.2;
      const nextSpan = Math.max(MIN_SPAN_MS, Math.min(viewSpan * factor, windowSpan));
      const ratio = (cursorMs - viewStart) / viewSpan;
      settle(panTo(cursorMs - ratio * nextSpan, nextSpan));
    };

    svg.addEventListener("wheel", onWheel, { passive: false });
    return () => svg.removeEventListener("wheel", onWheel);
  }, [viewStart, viewSpan, plotW, windowSpan, panTo, settle]);

  // --- Focus chart drag-to-pan ---------------------------------------------------------------------------------
  const onFocusPointerDown = (event: React.PointerEvent<SVGSVGElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (event.clientX - rect.left < LABEL_W) {
      return;
    }

    dragRef.current = { startX: event.clientX, viewStart, viewEnd };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const onFocusPointerMove = (event: React.PointerEvent<SVGSVGElement>) => {
    const drag = dragRef.current;
    if (drag === null) {
      return;
    }

    const span = drag.viewEnd - drag.viewStart;
    const deltaMs = -((event.clientX - drag.startX) / plotW) * span;
    settle(panTo(drag.viewStart + deltaMs, span));
  };

  const endFocusDrag = (event: React.PointerEvent<SVGSVGElement>) => {
    if (dragRef.current !== null) {
      dragRef.current = null;
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  };

  // --- Navigator brush -----------------------------------------------------------------------------------------
  const onNavPointerDown = (event: React.PointerEvent<SVGSVGElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const raw = event.clientX - rect.left;
    // Ignore the label gutter, but keep a HANDLE_HIT margin so the left handle, which sits right on the plot's
    // left edge when fully zoomed out, is still grabbable. Clamp into the plot so edge clicks snap to the handle.
    if (raw < LABEL_W - HANDLE_HIT) {
      return;
    }

    const mouseX = Math.max(LABEL_W, Math.min(raw, width));
    const selLeft = navXOf(viewStart);
    const selRight = navXOf(viewEnd);
    let mode: NavMode;
    if (Math.abs(mouseX - selLeft) <= HANDLE_HIT) {
      mode = "left";
    } else if (Math.abs(mouseX - selRight) <= HANDLE_HIT) {
      mode = "right";
    } else if (mouseX > selLeft && mouseX < selRight) {
      mode = "pan";
    } else {
      // Click on empty track: recenter the current span on the click, then let the drag pan from there.
      const clickedMs = windowStartMs + ((mouseX - LABEL_W) / plotW) * windowSpan;
      const [ns, ne] = panTo(clickedMs - viewSpan / 2, viewSpan);
      settle([ns, ne]);
      navDragRef.current = { mode: "pan", startX: event.clientX, viewStart: ns, viewEnd: ne };
      event.currentTarget.setPointerCapture(event.pointerId);
      return;
    }

    navDragRef.current = { mode, startX: event.clientX, viewStart, viewEnd };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const onNavPointerMove = (event: React.PointerEvent<SVGSVGElement>) => {
    const drag = navDragRef.current;
    if (drag === null) {
      return;
    }

    const deltaMs = ((event.clientX - drag.startX) / plotW) * windowSpan;
    if (drag.mode === "pan") {
      settle(panTo(drag.viewStart + deltaMs, drag.viewEnd - drag.viewStart));
    } else if (drag.mode === "left") {
      settle(resizeTo(drag.viewStart + deltaMs, drag.viewEnd));
    } else {
      settle(resizeTo(drag.viewStart, drag.viewEnd + deltaMs));
    }
  };

  const endNavDrag = (event: React.PointerEvent<SVGSVGElement>) => {
    if (navDragRef.current !== null) {
      navDragRef.current = null;
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  };

  const ticks = useMemo(() => makeTicks(viewStart, viewEnd), [viewStart, viewEnd]);
  const dayColumns = useMemo(() => {
    const cols: number[] = [];
    for (let d = localMidnight(viewStart); d < viewEnd; d += DAY_MS) {
      cols.push(d);
    }

    return cols;
  }, [viewStart, viewEnd]);

  // The free window on every day in view, so the gap a new schedule could take shows where it falls between the bars.
  // Each band starts at the window's local hour and one that crosses midnight runs on into the next day; the day before
  // the view is included so such a band still shows at the view's left edge. Local clock arithmetic (setHours), not
  // fixed 24-hour steps, so a daylight-saving day keeps the band on its hours.
  const freeBands = useMemo(() => {
    if (freeWindow === null) {
      return [];
    }

    const bands: { startMs: number; endMs: number }[] = [];
    const day = new Date(localMidnight(viewStart));
    day.setDate(day.getDate() - 1);
    while (day.getTime() < viewEnd) {
      const start = new Date(day);
      start.setHours(freeWindow.startHour);
      const end = new Date(start);
      end.setHours(start.getHours() + freeWindow.freeHours);
      if (end.getTime() > viewStart && start.getTime() < viewEnd) {
        bands.push({ startMs: Math.max(viewStart, start.getTime()), endMs: Math.min(viewEnd, end.getTime()) });
      }

      day.setDate(day.getDate() + 1);
    }

    return bands;
  }, [freeWindow, viewStart, viewEnd]);
  const focusHeight = AXIS_H + rows.length * LANE_H + 6;
  const showingAll = viewStart <= windowStartMs + 1000 && viewEnd >= windowEndMs - 1000;
  // The two view choices only mean something when the window is wider than the latest days; a 24-hour window is both.
  const canFocus = windowSpan > FOCUS_DAYS * DAY_MS + 1000;
  const windowDays = Math.round(windowSpan / DAY_MS);
  const nowX = xOf(nowMs);
  const selLeft = navXOf(viewStart);
  const selRight = navXOf(viewEnd);

  const showHover = (bar: RunBar) => (event: React.MouseEvent) => {
    const rect = containerRef.current?.getBoundingClientRect();
    if (rect === undefined) {
      return;
    }

    setHover({ bar, x: event.clientX - rect.left, y: event.clientY - rect.top });
  };

  // Double-click the plot to drill into the day under the cursor: the fast path to a single-day breakdown when
  // dragging the navigator edges would be fiddly.
  const onFocusDoubleClick = (event: React.MouseEvent<SVGSVGElement>) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (rect === undefined) {
      return;
    }

    const mouseX = event.clientX - rect.left;
    if (mouseX < LABEL_W) {
      return;
    }

    zoomToDay(viewStart + ((mouseX - LABEL_W) / plotW) * viewSpan);
  };

  return (
    <div ref={containerRef} className="relative w-full" data-testid="schedule-timeline-chart">
      {/* Focus chart: only the lanes scroll, so the navigator below stays put. */}
      <div className="overflow-y-auto" style={{ maxHeight: FOCUS_MAX_H }}>
        <svg
          ref={svgRef}
          width={width}
          height={focusHeight}
          role="img"
          aria-label="Schedule execution timeline"
          style={{ display: "block", touchAction: "none", cursor: "grab" }}
          onPointerDown={onFocusPointerDown}
          onPointerMove={onFocusPointerMove}
          onPointerUp={endFocusDrag}
          onPointerCancel={endFocusDrag}
          onDoubleClick={onFocusDoubleClick}
        >
          {freeBands.map((band) => (
            <rect
              key={`free-${band.startMs}`}
              x={xOf(band.startMs)}
              y={AXIS_H}
              width={Math.max(0, xOf(band.endMs) - xOf(band.startMs))}
              height={focusHeight - AXIS_H}
              fill={palette.primary}
              fillOpacity={0.1}
              pointerEvents="none"
              data-testid="timeline-free-band"
            />
          ))}

          {ticks.map((tick) => {
            const x = xOf(tick.ms);
            return (
              <g key={tick.ms}>
                <line
                  x1={x}
                  y1={AXIS_H}
                  x2={x}
                  y2={focusHeight}
                  stroke={palette.border}
                  strokeWidth={1}
                  strokeOpacity={tick.emphasized ? 0.9 : 0.45}
                />
                <text
                  x={x + 4}
                  y={AXIS_H - 10}
                  fill={palette.muted}
                  fontSize={11}
                  fontWeight={tick.emphasized ? 600 : 400}
                >
                  {tick.label}
                </text>
              </g>
            );
          })}

          {/* Clickable day headers: the axis band splits into one hit-cell per visible local day. Clicking a cell
              drills into that day; the cell stops the pointerdown so it never starts a pan. */}
          {dayColumns.map((day) => {
            const x0 = Math.max(LABEL_W, xOf(day));
            const x1 = Math.min(width, xOf(day + DAY_MS));
            if (x1 <= x0) {
              return null;
            }

            return (
              <rect
                key={`day-${day}`}
                x={x0}
                y={0}
                width={x1 - x0}
                height={AXIS_H}
                fill={hoverDay === day ? palette.primary : "transparent"}
                fillOpacity={hoverDay === day ? 0.1 : 0}
                style={{ cursor: "zoom-in" }}
                onPointerDown={(event) => event.stopPropagation()}
                onMouseEnter={() => setHoverDay(day)}
                onMouseLeave={() => setHoverDay((current) => (current === day ? null : current))}
                onClick={() => zoomToDay(day)}
              >
                <title>{`Zoom to ${weekdayDay.format(new Date(day))}`}</title>
              </rect>
            );
          })}

          {rows.map((row, index) => {
            const laneY = AXIS_H + index * LANE_H;
            const centerY = laneY + LANE_H / 2;
            return (
              <g key={row.scheduleId}>
                {index % 2 === 1 && (
                  <rect x={0} y={laneY} width={width} height={LANE_H} fill={palette.foreground} fillOpacity={0.03} />
                )}
                <circle cx={16} cy={centerY} r={4} fill={statusColor(row.lastStatus)} />
                <text
                  x={30}
                  y={centerY + 4}
                  fill={row.enabled && !row.paused ? palette.foreground : palette.muted}
                  fontSize={12}
                  fontWeight={500}
                >
                  {row.label.length > 24 ? `${row.label.slice(0, 23)}…` : row.label}
                </text>

                {/* A disabled or paused schedule keeps the next fire it had when it stopped, an instant it will not fire
                    at: only a schedule that is running marks one. */}
                {row.enabled && !row.paused && row.nextFireMs !== null && row.nextFireMs >= viewStart && row.nextFireMs <= viewEnd && (
                  <circle
                    cx={xOf(row.nextFireMs)}
                    cy={centerY}
                    r={4}
                    fill="none"
                    stroke={palette.primary}
                    strokeWidth={1.5}
                    strokeDasharray="2 1.5"
                  >
                    <title>{`Next fire ${dateTimeLabel.format(new Date(row.nextFireMs))}`}</title>
                  </circle>
                )}

                {row.bars.map((bar) => {
                  if (bar.endMs < viewStart || bar.startMs > viewEnd) {
                    return null;
                  }

                  const left = Math.max(LABEL_W, xOf(bar.startMs));
                  const right = Math.min(width, xOf(bar.endMs));
                  const barWidth = Math.max(MIN_BAR_W, right - left);
                  return (
                    <rect
                      key={bar.runId}
                      x={left}
                      y={centerY - BAR_H / 2}
                      width={barWidth}
                      height={BAR_H}
                      rx={3}
                      fill={statusColor(bar.status)}
                      stroke={palette.card}
                      strokeWidth={0.75}
                      style={{ cursor: "pointer" }}
                      onMouseEnter={showHover(bar)}
                      onMouseMove={showHover(bar)}
                      onMouseLeave={() => setHover(null)}
                    />
                  );
                })}
              </g>
            );
          })}

          {nowMs >= viewStart && nowMs <= viewEnd && (
            <g>
              <line
                x1={nowX}
                y1={AXIS_H - 2}
                x2={nowX}
                y2={focusHeight}
                stroke={palette.primary}
                strokeWidth={1.5}
                strokeDasharray="4 3"
              />
              <text x={nowX + 4} y={AXIS_H + 10} fill={palette.primary} fontSize={10} fontWeight={600}>
                now
              </text>
            </g>
          )}
        </svg>
      </div>

      {/* Navigator brush over the whole fetched window, with the two views it can jump to in its gutter. */}
      <div className="relative">
        <div
          className="absolute left-0 top-0 flex flex-col justify-center gap-1 pl-2"
          style={{ width: LABEL_W - 2 * HANDLE_HIT, height: NAV_H }}
        >
          <span className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">Overview</span>
          {canFocus && (
            <div className="flex items-center gap-1.5 text-xs" data-testid="timeline-view">
              <ViewChoice
                current={chosen.kind === "latest"}
                onClick={() => setChosen({ kind: "latest" })}
                testId="timeline-view-latest"
              >
                {`Latest ${FOCUS_DAYS} days`}
              </ViewChoice>
              <span aria-hidden className="text-muted-foreground">·</span>
              <ViewChoice
                current={showingAll}
                onClick={() => setChosen({ kind: "from", startMs: windowStartMs })}
                testId="timeline-view-all"
              >
                {`All ${windowDays} days`}
              </ViewChoice>
            </div>
          )}
        </div>
        <svg
          ref={navRef}
          width={width}
          height={NAV_H}
          role="presentation"
          style={{ display: "block", touchAction: "none" }}
          onPointerDown={onNavPointerDown}
          onPointerMove={onNavPointerMove}
          onPointerUp={endNavDrag}
          onPointerCancel={endNavDrag}
        >
          {/* Track */}
          <rect
            x={LABEL_W}
            y={NAV_LANE_TOP}
            width={Math.max(0, width - LABEL_W)}
            height={NAV_LANE_BOTTOM - NAV_LANE_TOP}
            fill={palette.foreground}
            fillOpacity={0.04}
            rx={3}
          />
  
          {/* Every run as a faint tick, so the busy stretches of the window are visible at a glance. */}
          {flatBars.map((bar) => (
            <line
              key={bar.runId}
              x1={navXOf(bar.startMs)}
              y1={NAV_LANE_TOP + 3}
              x2={navXOf(bar.startMs)}
              y2={NAV_LANE_BOTTOM - 3}
              stroke={statusColor(bar.status)}
              strokeWidth={1.25}
              strokeOpacity={0.55}
            />
          ))}
  
          {/* Selection window */}
          <rect
            x={selLeft}
            y={NAV_LANE_TOP}
            width={Math.max(2, selRight - selLeft)}
            height={NAV_LANE_BOTTOM - NAV_LANE_TOP}
            fill={palette.primary}
            fillOpacity={0.16}
            stroke={palette.primary}
            strokeWidth={1.5}
            rx={2}
            style={{ cursor: "grab" }}
            data-testid="timeline-view-selection"
          >
            <title>Drag to move the view, drag an edge to widen or narrow it</title>
          </rect>
          {[selLeft, selRight].map((x, i) => (
            <rect
              key={i === 0 ? "handle-left" : "handle-right"}
              x={x - 3}
              y={NAV_LANE_TOP - 1}
              width={6}
              height={NAV_LANE_BOTTOM - NAV_LANE_TOP + 2}
              rx={2}
              fill={palette.primary}
              style={{ cursor: "ew-resize" }}
            />
          ))}
        </svg>
      </div>

      {hover !== null && (
        <div
          className="pointer-events-none absolute z-10 max-w-60 rounded-md border bg-popover px-3 py-2 text-popover-foreground shadow-md"
          style={{
            left: Math.min(hover.x + 12, Math.max(0, width - 220)),
            top: hover.y + 12,
          }}
        >
          <div className="truncate font-mono text-[12px] font-medium">{hover.bar.flowName}</div>
          <div className="text-xs text-muted-foreground">
            {hover.bar.status}
            {hover.bar.durationSeconds !== null && (
              <span className="font-mono"> · {formatDurationSeconds(hover.bar.durationSeconds)}</span>
            )}
          </div>
          <div className="font-mono text-xs text-muted-foreground">
            {dateTimeLabel.format(new Date(hover.bar.startMs))}
          </div>
          {/* A lane holds every run of its schedule's flows, so say what started this one: the schedule on its own, a
              chain, or someone asking. */}
          {hover.bar.startedBy !== null && (
            <div className="mt-1 text-xs" data-testid="timeline-bar-started-by">{hover.bar.startedBy.summary}</div>
          )}
        </div>
      )}
    </div>
  );
}
