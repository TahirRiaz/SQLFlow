// The schedule timeline's planning figures and the span it shows: which hours of the day are free (counting every hour
// a run executes in, not only the hour it started), the busiest hour and how many runs start in it, the latest days the
// chart opens on, and how a chosen view holds as each refetch slides the window to now. Times are local, as the chart
// draws them.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  bestWindow, busiestHour, chosenView, FOCUS_DAYS, latestSpan, resolveView, type RunBar,
} from "../src/features/schedules/timeline.ts";

const MINUTE_MS = 60_000;
const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;

/** A local instant on a day with no daylight-saving change. */
function at(hour: number, minute = 0, day = 10): number {
  return new Date(2026, 10, day, hour, minute).getTime();
}

function bar(startMs: number, endMs: number): RunBar {
  return {
    runId: `run-${startMs}-${endMs}`,
    flowName: "logs",
    status: "succeeded",
    success: true,
    startMs,
    endMs,
    durationSeconds: (endMs - startMs) / 1000,
    startedBy: null,
  };
}

describe("the free window", () => {
  it("keeps every hour a run executes in busy, not only the hour it started", () => {
    // 07:30 to 09:10 keeps 07, 08 and 09 busy; 22:15 keeps 22. Counted by starts alone, 08:00-22:00 would look free.
    const window = bestWindow([bar(at(7, 30), at(9, 10)), bar(at(22, 15), at(22, 20))]);

    assert.deepEqual(window, { range: "10:00-22:00", startHour: 10, freeHours: 12 });
  });

  it("frees the hour a run ends exactly at", () => {
    const window = bestWindow([bar(at(7, 30), at(9, 0))]);

    assert.deepEqual(window, { range: "09:00-07:00", startHour: 9, freeHours: 22 });
  });

  it("finds a free stretch that runs across midnight as one", () => {
    const window = bestWindow([8, 12, 17].map((hour) => bar(at(hour, 10), at(hour, 20))));

    assert.deepEqual(window, { range: "18:00-08:00", startHour: 18, freeHours: 14 });
  });

  it("is none when something executes in every hour, or when there are no runs", () => {
    assert.equal(bestWindow([bar(at(6), at(6, 0, 11) + MINUTE_MS)]), null);
    assert.equal(bestWindow([]), null);
  });
});

describe("the busiest hour", () => {
  it("names the hour most runs start in and how many started then", () => {
    const busiest = busiestHour([bar(at(16, 5), at(16, 7)), bar(at(16, 40), at(17, 30)), bar(at(9), at(9, 1))]);

    assert.deepEqual(busiest, { range: "16:00-17:00", runCount: 2 });
  });

  it("is none without runs", () => {
    assert.equal(busiestHour([]), null);
  });
});

describe("the span the chart opens on", () => {
  const windowEndMs = at(18, 0, 14);
  const windowStartMs = windowEndMs - 14 * DAY_MS;

  it("is the latest days up to now when the newest run falls inside them", () => {
    const span = latestSpan([bar(windowEndMs - HOUR_MS, windowEndMs - HOUR_MS + MINUTE_MS)], windowStartMs, windowEndMs);

    assert.deepEqual(span, [windowEndMs - FOCUS_DAYS * DAY_MS, windowEndMs]);
  });

  it("ends a quarter of a day after the newest run when every run is older, so it never opens on an empty stretch", () => {
    const newestEndMs = windowEndMs - 5 * DAY_MS;
    const span = latestSpan([bar(newestEndMs - MINUTE_MS, newestEndMs)], windowStartMs, windowEndMs);

    assert.deepEqual(span, [newestEndMs + DAY_MS / 4 - FOCUS_DAYS * DAY_MS, newestEndMs + DAY_MS / 4]);
  });

  it("stays inside the window when the newest run is near its start", () => {
    const newestEndMs = windowStartMs + HOUR_MS;
    const span = latestSpan([bar(windowStartMs, newestEndMs)], windowStartMs, windowEndMs);

    assert.deepEqual(span, [windowStartMs, windowStartMs + FOCUS_DAYS * DAY_MS]);
  });

  it("is the whole window when the window is shorter than the latest days", () => {
    const span = latestSpan([bar(windowEndMs - HOUR_MS, windowEndMs)], windowEndMs - DAY_MS, windowEndMs);

    assert.deepEqual(span, [windowEndMs - DAY_MS, windowEndMs]);
  });
});

describe("a chosen view across refetches", () => {
  const windowEndMs = at(18, 0, 14);
  const windowStartMs = windowEndMs - 14 * DAY_MS;
  // The next refetch, thirty seconds on: the window slides forward by as much.
  const slide = 30_000;

  it("keeps following now when it reaches the end of the window", () => {
    const view = chosenView(windowEndMs - 6 * HOUR_MS, windowEndMs - 500, windowEndMs);

    assert.deepEqual(view, { kind: "from", startMs: windowEndMs - 6 * HOUR_MS });
    assert.deepEqual(
      resolveView(view, [], windowStartMs + slide, windowEndMs + slide),
      [windowEndMs - 6 * HOUR_MS, windowEndMs + slide],
    );
  });

  it("stays where it was put when it ends before now", () => {
    const view = chosenView(at(0, 0, 12), at(0, 0, 13), windowEndMs);

    assert.deepEqual(view, { kind: "between", startMs: at(0, 0, 12), endMs: at(0, 0, 13) });
    assert.deepEqual(resolveView(view, [], windowStartMs + slide, windowEndMs + slide), [at(0, 0, 12), at(0, 0, 13)]);
  });

  it("shows the whole window, refetch after refetch, once it starts at the window's start", () => {
    const view = chosenView(windowStartMs, windowEndMs, windowEndMs);

    assert.deepEqual(
      resolveView(view, [], windowStartMs + slide, windowEndMs + slide),
      [windowStartMs + slide, windowEndMs + slide],
    );
  });

  it("slides along at its own width when the window has moved past its start", () => {
    const view = chosenView(windowStartMs, windowStartMs + DAY_MS, windowEndMs);

    assert.deepEqual(
      resolveView(view, [], windowStartMs + slide, windowEndMs + slide),
      [windowStartMs + slide, windowStartMs + slide + DAY_MS],
    );
  });

  it("follows the newest runs while it is the latest view", () => {
    const newest = bar(windowEndMs + slide - MINUTE_MS, windowEndMs + slide);

    assert.deepEqual(
      resolveView({ kind: "latest" }, [newest], windowStartMs + slide, windowEndMs + slide),
      [windowEndMs + slide - FOCUS_DAYS * DAY_MS, windowEndMs + slide],
    );
  });
});
