// How long runs take, as a list or a header can honestly say it. Pure and framework-free, so the arithmetic is tested
// without a DOM. Every API timestamp is UTC by contract and goes through parseUtc before it becomes a number.

import type { RunStatus } from "../api/types.ts";
import { parseUtc } from "./time.ts";

/** The fields of a run its timing reads. */
export interface TimedRun {
  status: RunStatus;
  groupId: string | null;
  startUtc: string | null;
  endUtc: string | null;
  durationSeconds: number | null;
}

const TERMINAL: ReadonlySet<RunStatus> = new Set<RunStatus>(["succeeded", "failed", "cancelled", "skipped"]);

/** The seconds from `fromUtc` to the instant `toMs`, never negative: a clock that runs a little behind the server's must
 * not show a run as having run for less than nothing. */
export function secondsSince(fromUtc: string, toMs: number): number {
  return Math.max(0, (toMs - parseUtc(fromUtc).getTime()) / 1000);
}

/** The seconds a run waited between being asked for and starting, or null when either instant is unknown. */
export function waitedSeconds(enqueuedUtc: string | null | undefined, startUtc: string | null | undefined): number | null {
  if (!enqueuedUtc || !startUtc) {
    return null;
  }

  return secondsSince(enqueuedUtc, parseUtc(startUtc).getTime());
}

/**
 * What a set of runs says about time:
 * - `took`: they ran as one execution (the members of one run group, or a single run) and all ended, so the set took
 *   from its first start to its last end. `summedSeconds` is what their durations add up to, which is more than the
 *   span when members ran side by side.
 * - `running`: they ran as one execution and some are still queued or running; it has run since `startedUtc`.
 * - `summed`: they did not run together (each flow's latest run, say), so no span between them means anything; their
 *   durations added up say how much work they were.
 * - `none`: nothing in the set has started or been timed.
 */
export type SetTiming =
  | { kind: "took"; seconds: number; startedUtc: string; endedUtc: string; summedSeconds: number | null }
  | { kind: "running"; startedUtc: string }
  | { kind: "summed"; seconds: number; timedRuns: number }
  | { kind: "none" };

function earliest(values: readonly (string | null)[]): string | null {
  let best: string | null = null;
  for (const value of values) {
    if (value !== null && (best === null || parseUtc(value).getTime() < parseUtc(best).getTime())) {
      best = value;
    }
  }

  return best;
}

function latest(values: readonly (string | null)[]): string | null {
  let best: string | null = null;
  for (const value of values) {
    if (value !== null && (best === null || parseUtc(value).getTime() > parseUtc(best).getTime())) {
      best = value;
    }
  }

  return best;
}

export function setTiming(runs: readonly TimedRun[]): SetTiming {
  if (runs.length === 0) {
    return { kind: "none" };
  }

  const timed = runs.filter((run) => run.durationSeconds !== null);
  const summed = timed.reduce((sum, run) => sum + (run.durationSeconds ?? 0), 0);
  const groupIds = new Set(runs.map((run) => run.groupId));
  const oneExecution = runs.length === 1 || (groupIds.size === 1 && !groupIds.has(null));

  if (!oneExecution) {
    return timed.length === 0 ? { kind: "none" } : { kind: "summed", seconds: summed, timedRuns: timed.length };
  }

  const startedUtc = earliest(runs.map((run) => run.startUtc));
  if (startedUtc === null) {
    return { kind: "none" };
  }

  if (runs.some((run) => !TERMINAL.has(run.status))) {
    return { kind: "running", startedUtc };
  }

  const endedUtc = latest(runs.map((run) => run.endUtc));
  if (endedUtc === null) {
    return { kind: "none" };
  }

  return {
    kind: "took",
    seconds: secondsSince(startedUtc, parseUtc(endedUtc).getTime()),
    startedUtc,
    endedUtc,
    summedSeconds: timed.length === 0 ? null : summed,
  };
}
