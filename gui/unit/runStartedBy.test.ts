// What a row of the runs board shows for what started a run: who or what in a few words, then a quiet qualifier. The
// control plane decides what started a run and words the whole answer (the row's hover); these are the few words, and
// the filter's choices, which must cover every kind the control plane answers.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { RunStartedBy } from "../src/api/types.ts";
import { isStartedByKind, startedByFace, startedByFilterOptions } from "../src/features/runs/startedBy.ts";

function startedBy(overrides: Partial<RunStartedBy> & Pick<RunStartedBy, "kind">): RunStartedBy {
  return {
    summary: "",
    who: null,
    scheduleId: null,
    scheduleName: null,
    afterRunId: null,
    afterScheduleName: null,
    afterFlowName: null,
    triggerRequestId: null,
    ...overrides,
  };
}

describe("a row's face of what started a run", () => {
  it("names the schedule for its own fire", () => {
    assert.deepEqual(
      startedByFace(startedBy({ kind: "schedule", scheduleName: "nightly" })),
      { primary: "nightly", secondary: "on its own" });
  });

  it("names the schedule and the one it followed for a chained fire", () => {
    assert.deepEqual(
      startedByFace(startedBy({ kind: "chain", scheduleName: "reference-data", afterScheduleName: "land-files" })),
      { primary: "reference-data", secondary: "after land-files" });
    assert.deepEqual(
      startedByFace(startedBy({ kind: "chain", scheduleName: "reference-data" })),
      { primary: "reference-data", secondary: "after its parents" });
  });

  it("names the person, and the schedule they ran now", () => {
    assert.deepEqual(startedByFace(startedBy({ kind: "user", who: "alice" })), { primary: "alice", secondary: null });
    assert.deepEqual(
      startedByFace(startedBy({ kind: "user", who: "alice", scheduleName: "nightly" })),
      { primary: "alice", secondary: "ran nightly" });
    assert.deepEqual(startedByFace(startedBy({ kind: "user" })), { primary: "unnamed caller", secondary: null });
  });

  it("names the external trigger, and the schedule it fired", () => {
    assert.deepEqual(
      startedByFace(startedBy({ kind: "trigger", who: "merge-to-main", scheduleName: "nightly" })),
      { primary: "merge-to-main", secondary: "fired nightly" });
    assert.deepEqual(
      startedByFace(startedBy({ kind: "trigger", who: "merge-to-main" })),
      { primary: "merge-to-main", secondary: "trigger" });
  });

  it("names the command-line account when the run recorded one", () => {
    assert.deepEqual(
      startedByFace(startedBy({ kind: "cli", who: "tahir@build-01" })),
      { primary: "tahir@build-01", secondary: "command line" });
    assert.deepEqual(startedByFace(startedBy({ kind: "cli" })), { primary: "command line", secondary: null });
  });

  it("says a run from before the record was not recorded", () => {
    assert.deepEqual(startedByFace(startedBy({ kind: "unknown" })), { primary: "not recorded", secondary: null });
  });
});

describe("the Started by filter", () => {
  it("offers every kind the control plane answers, once", () => {
    const kinds = startedByFilterOptions.map((option) => option.value);
    assert.deepEqual([...kinds].sort(), ["chain", "cli", "schedule", "trigger", "unknown", "user"]);
  });

  it("takes back only a kind it offers", () => {
    assert.equal(isStartedByKind("user"), true);
    assert.equal(isStartedByKind("manual"), false);
    assert.equal(isStartedByKind(""), false);
  });
});
