// Pure, framework-free helpers behind the schedules timeline: they turn the runs list and the schedule list into
// per-schedule lanes of run bars, derive the summary figures an operator plans a new schedule against (the free
// window, the busiest hour, the average run), and decide which span the chart shows. Kept side-effect-free so the
// chart and the page share one source of truth and the math is testable without a DOM. Every API timestamp is UTC by
// contract and goes through parseUtc before it becomes a number.

import type { RunStartedBy, RunStatus, RunSummary, Schedule } from "../../api/types.ts";
import { parseUtc } from "../../lib/time.ts";

/** A selectable history window. `days` bounds the runs the timeline fetches (WrittenUtc within [now-days, now]). */
export interface RangePreset {
  key: string;
  label: string;
  days: number;
}

export const RANGE_PRESETS: readonly RangePreset[] = [
  { key: "24h", label: "Last 24 hours", days: 1 },
  { key: "7d", label: "Last 7 days", days: 7 },
  { key: "14d", label: "Last 14 days", days: 14 },
  { key: "30d", label: "Last 30 days", days: 30 },
];

export const DEFAULT_RANGE_KEY = "14d";

const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;

/** The status tones the timeline draws with (DESIGN.md 3.2): status colors are reserved for run states, and
 * cancelled/skipped/none read as muted. One mapping shared by the chart and the page's legend. */
export type StatusTone = "success" | "destructive" | "info" | "warning" | "muted";

export function statusTone(status: RunStatus | null): StatusTone {
  switch (status) {
    case "succeeded":
      return "success";
    case "failed":
      return "destructive";
    case "running":
      return "info";
    case "queued":
      return "warning";
    default:
      return "muted";
  }
}

export function rangeByKey(key: string): RangePreset {
  return RANGE_PRESETS.find((r) => r.key === key) ?? RANGE_PRESETS[2];
}

/** The [from, to] epoch-ms window a preset covers, ending at `nowMs`. */
export function windowBounds(nowMs: number, days: number): { fromMs: number; toMs: number } {
  return { fromMs: nowMs - days * DAY_MS, toMs: nowMs };
}

/** One run drawn as a bar: its execution window [startMs, endMs] and the status that colors it, so a bar's width reads
 * as how long the run took. The window is the run's own start and end when it carries them; otherwise its outcome
 * instant (WrittenUtc) back-dated by its duration. A run with neither (still queued, or one that never started)
 * collapses to a zero-width marker the chart widens to a minimum so it is still visible. */
export interface RunBar {
  runId: string;
  flowName: string;
  status: RunStatus;
  success: boolean;
  startMs: number;
  endMs: number;
  durationSeconds: number | null;
  /** What started the run: the schedule on its own, behind its parents, or someone asking. A lane holds every run of
   * its schedule's flows, so a bar there was not necessarily the schedule's own fire. */
  startedBy: RunStartedBy | null;
}

/** One lane of the timeline: a schedule and the runs of its flow that fell inside the window. */
export interface TimelineRow {
  scheduleId: string;
  label: string;
  trigger: string;
  enabled: boolean;
  paused: boolean;
  nextFireMs: number | null;
  lastStatus: RunStatus | null;
  bars: RunBar[];
}

/** The quietest stretch of the day: the span itself, the local hour it starts at, and how many hours it covers. */
export interface QuietWindow {
  range: string;
  startHour: number;
  freeHours: number;
}

/** The local hour most runs start in, as "HH:00-HH:00", and how many runs of the window started in it. */
export interface BusiestHour {
  range: string;
  runCount: number;
}

export interface TimelineStats {
  scheduleCount: number;
  activeScheduleCount: number;
  runCount: number;
  /** Share of runs that succeeded, 0..100; null when the window holds no runs. */
  successRate: number | null;
  avgDurationSeconds: number | null;
  busiestHour: BusiestHour | null;
  bestWindow: QuietWindow | null;
}

/** The human trigger label for a schedule, matching the wording the Schedules table uses: its cron, its interval, or
 * the schedules it chains behind. */
export function triggerLabel(schedule: Schedule): string {
  if (schedule.cron !== null) {
    return `cron: ${schedule.cron}`;
  }

  if (schedule.intervalSeconds !== null) {
    return `every ${schedule.intervalSeconds}s`;
  }

  const parents = schedule.afterSchedules ?? [];
  return parents.length > 0 ? `after ${parents.join(", ")}` : "manual";
}

function toBar(run: RunSummary): RunBar {
  const endMs = parseUtc(run.endUtc ?? run.writtenUtc).getTime();
  const durationMs = run.durationSeconds !== null ? Math.max(0, run.durationSeconds) * 1000 : 0;
  // A start recorded after the end (two clocks that disagree) draws a marker at the end rather than a bar running backwards.
  const startMs = run.startUtc !== null ? Math.min(parseUtc(run.startUtc).getTime(), endMs) : endMs - durationMs;
  return {
    runId: run.runId,
    flowName: run.flowName,
    status: run.status,
    success: run.success,
    startMs,
    endMs,
    durationSeconds: run.durationSeconds,
    startedBy: run.startedBy,
  };
}

/**
 * Builds one lane per schedule, filling each with the runs of every flow that joined it. A schedule fires its whole
 * member set as one group, so the lane has to span all of them: showing one flow's runs would hide most of what the
 * fire actually did. Lanes are ordered busiest-first (then by name) so the schedules that actually run rise to the
 * top and idle ones settle below, the same reading order the reference timeline uses. A schedule with no runs in the
 * window still gets a lane, so its silence is visible rather than hidden.
 */
export function buildRows(schedules: readonly Schedule[], runs: readonly RunSummary[]): TimelineRow[] {
  const barsByPipeline = new Map<string, RunBar[]>();
  for (const run of runs) {
    const bar = toBar(run);
    const existing = barsByPipeline.get(run.pipelineId);
    if (existing === undefined) {
      barsByPipeline.set(run.pipelineId, [bar]);
    } else {
      existing.push(bar);
    }
  }

  const rows = schedules.map<TimelineRow>((schedule) => {
    const bars = schedule.memberPipelineIds
      .flatMap((pipelineId) => barsByPipeline.get(pipelineId) ?? [])
      .slice()
      .sort((a, b) => a.startMs - b.startMs);
    const last = bars.reduce<RunBar | null>((newest, bar) => (newest === null || bar.endMs > newest.endMs ? bar : newest), null);
    return {
      scheduleId: schedule.id,
      label: schedule.name,
      trigger: triggerLabel(schedule),
      enabled: schedule.enabled,
      paused: schedule.paused,
      nextFireMs: schedule.nextFireUtc !== null ? parseUtc(schedule.nextFireUtc).getTime() : null,
      lastStatus: last?.status ?? null,
      bars,
    };
  });

  rows.sort((a, b) => b.bars.length - a.bars.length || a.label.localeCompare(b.label));
  return rows;
}

function pad2(value: number): string {
  return value.toString().padStart(2, "0");
}

/** A 24-slot histogram of run start-hours in the viewer's local time (the hours an operator experiences). */
function hourHistogram(bars: readonly RunBar[]): number[] {
  const buckets = new Array<number>(24).fill(0);
  for (const bar of bars) {
    buckets[new Date(bar.startMs).getHours()] += 1;
  }

  return buckets;
}

/** The local hour with the most run starts, and how many started in it; null when there are no runs. */
export function busiestHour(bars: readonly RunBar[]): BusiestHour | null {
  if (bars.length === 0) {
    return null;
  }

  const buckets = hourHistogram(bars);
  let peak = 0;
  for (let hour = 1; hour < 24; hour += 1) {
    if (buckets[hour] > buckets[peak]) {
      peak = hour;
    }
  }

  return buckets[peak] === 0 ? null : { range: `${pad2(peak)}:00-${pad2((peak + 1) % 24)}:00`, runCount: buckets[peak] };
}

/** The local hours of the day in which some run was executing: every hour from the one a run started in through the
 * one it ended in, so a long run keeps the hours it overlaps busy, not only the hour it began. A run of a day or more
 * fills them all. */
function busyHours(bars: readonly RunBar[]): boolean[] {
  const busy = new Array<boolean>(24).fill(false);
  for (const bar of bars) {
    if (bar.endMs - bar.startMs >= DAY_MS) {
      return busy.fill(true);
    }

    const hourStart = new Date(bar.startMs);
    hourStart.setMinutes(0, 0, 0);
    let ms = hourStart.getTime();
    do {
      busy[new Date(ms).getHours()] = true;
      ms += HOUR_MS;
    } while (ms < bar.endMs);
  }

  return busy;
}

/** The longest contiguous run of local hours in which nothing was executing (wrapping across midnight): where a new
 * schedule has the estate to itself. The span as "HH:00-HH:00", the hour it starts at, and its length in hours. Null
 * when some run was executing in every hour, or there were no runs at all. */
export function bestWindow(bars: readonly RunBar[]): QuietWindow | null {
  if (bars.length === 0) {
    return null;
  }

  const busy = busyHours(bars);
  let bestLen = 0;
  let bestStart = 0;
  let curLen = 0;
  let curStart = 0;
  // Sweep 0..47 so a quiet span straddling midnight (e.g. 22:00 through 03:00) is found as one run.
  for (let i = 0; i < 48; i += 1) {
    const hour = i % 24;
    if (!busy[hour]) {
      if (curLen === 0) {
        curStart = hour;
      }

      curLen += 1;
      if (curLen > bestLen) {
        bestLen = curLen;
        bestStart = curStart;
      }
    } else {
      curLen = 0;
    }
  }

  if (bestLen === 0) {
    return null;
  }

  const len = Math.min(bestLen, 24);
  return { range: `${pad2(bestStart)}:00-${pad2((bestStart + len) % 24)}:00`, startHour: bestStart, freeHours: len };
}

/** Rolls the lanes up into the summary figures shown above the chart. */
export function computeStats(schedules: readonly Schedule[], rows: readonly TimelineRow[]): TimelineStats {
  const bars = rows.flatMap((row) => row.bars);
  const durations = bars.map((bar) => bar.durationSeconds).filter((d): d is number => d !== null);
  const succeeded = bars.filter((bar) => bar.success).length;
  return {
    scheduleCount: schedules.length,
    activeScheduleCount: schedules.filter((s) => s.enabled && !s.paused).length,
    runCount: bars.length,
    successRate: bars.length === 0 ? null : Math.round((succeeded / bars.length) * 100),
    avgDurationSeconds: durations.length === 0 ? null : durations.reduce((sum, d) => sum + d, 0) / durations.length,
    busiestHour: busiestHour(bars),
    bestWindow: bestWindow(bars),
  };
}

/** How many days the chart opens on: today and the day before, so a day's pattern reads beside the last one and a run
 * of a minute or two is a bar rather than a hairline. The overview under the chart keeps the whole window in reach. */
export const FOCUS_DAYS = 2;

/** What the chart shows. `latest` follows the newest runs and is what the chart opens on; `from` runs from a fixed
 * instant up to now, so it grows as the page refetches; `between` is a fixed span an operator zoomed or panned to. */
export type TimelineView =
  | { kind: "latest" }
  | { kind: "from"; startMs: number }
  | { kind: "between"; startMs: number; endMs: number };

/** The span the chart opens on: the last FOCUS_DAYS of the window. It ends at now when the newest run falls inside
 * that span; when every run in the window is older, it ends a quarter of a day after the newest one, so the chart
 * never opens on an empty stretch while its runs sit off to one side. A window shorter than FOCUS_DAYS is shown whole. */
export function latestSpan(bars: readonly RunBar[], windowStartMs: number, windowEndMs: number): [number, number] {
  const span = Math.min(FOCUS_DAYS * DAY_MS, windowEndMs - windowStartMs);
  const newestEndMs = bars.reduce<number | null>((newest, bar) => (newest === null || bar.endMs > newest ? bar.endMs : newest), null);
  const endMs = newestEndMs === null || newestEndMs >= windowEndMs - span
    ? windowEndMs
    : Math.min(windowEndMs, newestEndMs + DAY_MS / 4);
  const startMs = Math.max(windowStartMs, endMs - span);
  return [startMs, startMs + span];
}

/** The view a zoom or a pan settles on. One that reaches the end of the window keeps following now, so the next
 * refetch extends it to the newest runs rather than leaving them just off its right edge; any other stays put. */
export function chosenView(startMs: number, endMs: number, windowEndMs: number): TimelineView {
  return endMs >= windowEndMs - 1000 ? { kind: "from", startMs } : { kind: "between", startMs, endMs };
}

/** The [start, end] a view covers inside the current window. Each refetch slides the window forward to now, so a span
 * whose start has fallen out of it is clamped back in: a `from` view loses the part that left, a `between` view slides
 * along at its own width. */
export function resolveView(
  view: TimelineView,
  bars: readonly RunBar[],
  windowStartMs: number,
  windowEndMs: number,
): [number, number] {
  switch (view.kind) {
    case "latest":
      return latestSpan(bars, windowStartMs, windowEndMs);
    case "from":
      return [Math.max(windowStartMs, view.startMs), windowEndMs];
    case "between": {
      const span = Math.min(view.endMs - view.startMs, windowEndMs - windowStartMs);
      const startMs = Math.max(windowStartMs, Math.min(view.startMs, windowEndMs - span));
      return [startMs, startMs + span];
    }
  }
}
