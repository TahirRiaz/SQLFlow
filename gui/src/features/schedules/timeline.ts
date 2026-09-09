// Pure, framework-free helpers behind the schedules timeline: they turn the runs list and the schedule list into
// per-schedule lanes of run bars, and derive the insight-strip figures (busiest hour, best free window, success
// rate). Kept side-effect-free so the chart and the page can share one source of truth and the math is testable
// without a DOM. Every API timestamp is UTC by contract and goes through parseUtc before it becomes a number.

import type { RunStatus, RunSummary, Schedule } from "../../api/types";
import { parseUtc } from "../../lib/time";

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

const DAY_MS = 86_400_000;

export function rangeByKey(key: string): RangePreset {
  return RANGE_PRESETS.find((r) => r.key === key) ?? RANGE_PRESETS[2];
}

/** The [from, to] epoch-ms window a preset covers, ending at `nowMs`. */
export function windowBounds(nowMs: number, days: number): { fromMs: number; toMs: number } {
  return { fromMs: nowMs - days * DAY_MS, toMs: nowMs };
}

/** One run drawn as a bar: its execution window [startMs, endMs] and the status that colors it. The window is the
 * run's outcome instant (WrittenUtc) back-dated by its duration, so a bar's width reads as how long the run took;
 * a run with no recorded duration (still queued, or one that never timed) collapses to a zero-width marker the
 * chart widens to a minimum so it is still visible. */
export interface RunBar {
  runId: string;
  flowName: string;
  status: RunStatus;
  success: boolean;
  startMs: number;
  endMs: number;
  durationSeconds: number | null;
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

/** The quietest stretch of the day: the span itself, and how many hours it covers. */
export interface QuietWindow {
  range: string;
  freeHours: number;
}

export interface TimelineStats {
  scheduleCount: number;
  activeScheduleCount: number;
  runCount: number;
  /** Share of runs that succeeded, 0..100; null when the window holds no runs. */
  successRate: number | null;
  avgDurationSeconds: number | null;
  busiestHour: string | null;
  bestWindow: QuietWindow | null;
}

/** The human trigger label for a schedule, matching the wording the Schedules table uses. */
export function triggerLabel(schedule: Schedule): string {
  if (schedule.cron !== null) {
    return `cron: ${schedule.cron}`;
  }

  return schedule.intervalSeconds !== null ? `every ${schedule.intervalSeconds}s` : "manual";
}

function toBar(run: RunSummary): RunBar {
  const endMs = parseUtc(run.writtenUtc).getTime();
  const durationMs = run.durationSeconds !== null ? Math.max(0, run.durationSeconds) * 1000 : 0;
  return {
    runId: run.runId,
    flowName: run.flowName,
    status: run.status,
    success: run.success,
    startMs: endMs - durationMs,
    endMs,
    durationSeconds: run.durationSeconds,
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

/** The local hour with the most run starts, as "HH:00-HH:00"; null when there are no runs. */
export function busiestHour(bars: readonly RunBar[]): string | null {
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

  return buckets[peak] === 0 ? null : `${pad2(peak)}:00-${pad2((peak + 1) % 24)}:00`;
}

/** The longest contiguous run of zero-activity local hours (wrapping across midnight): the span as
 * "HH:00-HH:00" plus its length in hours. Null when every hour saw a run, or there were no runs at all. */
export function bestWindow(bars: readonly RunBar[]): QuietWindow | null {
  if (bars.length === 0) {
    return null;
  }

  const buckets = hourHistogram(bars);
  let bestLen = 0;
  let bestStart = 0;
  let curLen = 0;
  let curStart = 0;
  // Sweep 0..47 so a quiet span straddling midnight (e.g. 22:00 through 03:00) is found as one run.
  for (let i = 0; i < 48; i += 1) {
    const hour = i % 24;
    if (buckets[hour] === 0) {
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
  return { range: `${pad2(bestStart)}:00-${pad2((bestStart + len) % 24)}:00`, freeHours: len };
}

/** Rolls the lanes up into the insight-strip figures shown above the chart. */
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
