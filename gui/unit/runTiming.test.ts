// How long a set of runs took, as the run board's headers word it: one fire took from its first start to its last end,
// counts up while a member still runs, and runs that did not run together only add up their run time.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { secondsSince, setTiming, waitedSeconds, type TimedRun } from "../src/lib/runTiming.ts";

function run(overrides: Partial<TimedRun>): TimedRun {
  return { status: "succeeded", groupId: "g1", startUtc: null, endUtc: null, durationSeconds: null, ...overrides };
}

describe("setTiming", () => {
  it("times one fire from its first start to its last end, not by its members' durations added up", () => {
    const timing = setTiming([
      run({ startUtc: "2026-06-19T09:01:00Z", endUtc: "2026-06-19T09:03:00Z", durationSeconds: 120 }),
      run({ startUtc: "2026-06-19T09:02:00Z", endUtc: "2026-06-19T09:10:00Z", durationSeconds: 480 }),
      run({ status: "skipped", endUtc: "2026-06-19T09:04:00Z" }),
    ]);

    assert.deepEqual(timing, {
      kind: "took",
      seconds: 540,
      startedUtc: "2026-06-19T09:01:00Z",
      endedUtc: "2026-06-19T09:10:00Z",
      summedSeconds: 600,
    });
  });

  it("reads suffix-less instants as UTC, as every API instant is", () => {
    const timing = setTiming([run({ startUtc: "2026-06-19T09:00:00", endUtc: "2026-06-19T09:00:30Z", durationSeconds: 30 })]);
    assert.equal(timing.kind === "took" && timing.seconds, 30);
  });

  it("counts up from the first start while a member of the fire still runs", () => {
    const timing = setTiming([
      run({ startUtc: "2026-06-19T09:01:00Z", endUtc: "2026-06-19T09:03:00Z", durationSeconds: 120 }),
      run({ status: "running", startUtc: "2026-06-19T09:02:00Z" }),
    ]);

    assert.deepEqual(timing, { kind: "running", startedUtc: "2026-06-19T09:01:00Z" });
  });

  it("adds up the run time of runs that did not run together, since no span between them means anything", () => {
    const timing = setTiming([
      run({ groupId: "g1", startUtc: "2026-06-18T09:00:00Z", endUtc: "2026-06-18T09:01:00Z", durationSeconds: 60 }),
      run({ groupId: null, startUtc: "2026-06-19T09:00:00Z", endUtc: "2026-06-19T09:00:30Z", durationSeconds: 30 }),
      run({ groupId: "g2", status: "skipped" }),
    ]);

    assert.deepEqual(timing, { kind: "summed", seconds: 90, timedRuns: 2 });
  });

  it("says nothing of a set none of which has started", () => {
    assert.deepEqual(setTiming([run({ status: "queued" }), run({ status: "queued" })]), { kind: "none" });
    assert.deepEqual(setTiming([]), { kind: "none" });
  });

  it("times a single run as an execution of its own, grouped or not", () => {
    const timing = setTiming([
      run({ groupId: null, startUtc: "2026-06-19T09:00:00Z", endUtc: "2026-06-19T09:00:12.500Z", durationSeconds: 12.5 }),
    ]);
    assert.equal(timing.kind, "took");
    assert.equal(timing.kind === "took" && timing.seconds, 12.5);
  });
});

describe("secondsSince and waitedSeconds", () => {
  it("never reports less than nothing when the browser's clock runs behind the server's", () => {
    assert.equal(secondsSince("2026-06-19T09:00:10Z", Date.parse("2026-06-19T09:00:00Z")), 0);
    assert.equal(secondsSince("2026-06-19T09:00:00Z", Date.parse("2026-06-19T09:01:30Z")), 90);
  });

  it("says how long a run waited before it started, or nothing without both instants", () => {
    assert.equal(waitedSeconds("2026-06-19T09:00:00Z", "2026-06-19T09:00:45Z"), 45);
    assert.equal(waitedSeconds(null, "2026-06-19T09:00:45Z"), null);
    assert.equal(waitedSeconds("2026-06-19T09:00:00Z", null), null);
  });
});
