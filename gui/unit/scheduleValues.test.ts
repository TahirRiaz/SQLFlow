// What a schedule gives each flow it runs, as the Schedules page and the Run now dialog word it: where each value comes
// from, a member's values as one line, how many members' runs would be refused, and the schedule's own values.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ScheduleMemberParameters } from "../src/api/types.ts";
import {
  memberValuesLine, originLabel, refusedMembers, scheduleValueAssignments, shownValues, valueText,
} from "../src/features/schedules/scheduleValues.ts";

function member(overrides: Partial<ScheduleMemberParameters>): ScheduleMemberParameters {
  return {
    flowName: "logs",
    flowKind: "delivery",
    wave: 0,
    pipelineId: "p1",
    takesScheduleArguments: true,
    operation: "deliver",
    parameters: [],
    values: [],
    settings: [],
    kindValues: [],
    note: null,
    problems: [],
    ...overrides,
  };
}

describe("a value's origin", () => {
  it("says whether the schedule, the flow's default or nothing gives it, and what refuses it", () => {
    assert.equal(originLabel("Given"), "from the schedule");
    assert.equal(originLabel("Default"), "the flow's default");
    assert.equal(originLabel("Unset"), "not set");
    assert.equal(originLabel("Missing"), "missing: required");
    assert.equal(originLabel("NotDeclared"), "not declared by the flow");
    assert.equal(originLabel("Kind"), "from the schedule, for the kind");
  });
});

describe("a member's values", () => {
  const logs = member({
    values: [
      { name: "logSource", value: ["STAT_COMP", "STAT_CPI"], origin: "Given", problem: null },
      { name: "region", value: "north", origin: "Default", problem: null },
      { name: "label", value: null, origin: "Unset", problem: null },
      { name: "site", value: null, origin: "Missing", problem: "parameter 'site' is required." },
      { name: "partition", value: "dev", origin: "Kind", problem: null },
    ],
    // The control plane lists every value's problem among the member's own, as a fire would meet them.
    problems: ["parameter 'site' is required."],
  });

  it("show every value the run takes, and leave out a parameter it takes none for", () => {
    assert.deepEqual(shownValues(logs).map((v) => v.name), ["logSource", "region", "site", "partition"]);
  });

  it("read as one line, a default marked and a missing value asked for", () => {
    assert.equal(memberValuesLine(logs), "logSource=STAT_COMP, STAT_CPI · region=north (default) · site=? · partition=dev");
    assert.equal(memberValuesLine(member({})), "no parameters");
  });

  it("are a hyphen when there is no value", () => {
    assert.equal(valueText(null), "-");
    assert.equal(valueText(["a", "b"]), "a, b");
  });

  it("count the members whose run would be refused", () => {
    assert.equal(refusedMembers([logs, member({ flowName: "other" }), member({ flowName: "x", problems: ["no"] })]), 2);
    assert.equal(refusedMembers([]), 0);
  });
});

describe("a schedule's own values", () => {
  it("read as assignments in name order, a list in brackets", () => {
    assert.deepEqual(scheduleValueAssignments({ region: ["north", "south"], logSource: "STAT_COMP" }), ["logSource=STAT_COMP", "region=[north, south]"]);
    assert.deepEqual(scheduleValueAssignments({}), []);
  });
});
