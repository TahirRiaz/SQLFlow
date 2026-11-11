// A schedule's fire in waves, as the Pipelines page filters and labels its flows: numbered the way the run board numbers
// them, from the plan's waves rather than each flow's lineage wave.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { SchedulePlanMember } from "../src/api/types.ts";
import { scheduleWaves } from "../src/features/pipelines/scheduleWaves.ts";

function member(pipelineId: string | null, wave: number): SchedulePlanMember {
  return { flowName: pipelineId ?? "unresolved", flowKind: "ing", wave, batch: "default", pipelineId };
}

describe("scheduleWaves", () => {
  it("numbers the plan's waves from 1 in order and counts the flows of each", () => {
    const waves = scheduleWaves([member("land", 0), member("ingest-a", 1), member("ingest-b", 1), member("deliver", 2)]);

    assert.deepEqual(waves.waves, [{ number: 1, flows: 1 }, { number: 2, flows: 2 }, { number: 3, flows: 1 }]);
    assert.equal(waves.waveOf.get("land"), 1);
    assert.equal(waves.waveOf.get("ingest-b"), 2);
    assert.equal(waves.waveOf.get("deliver"), 3);
  });

  it("numbers by position, as the run board does, whatever numbers the plan gives its waves", () => {
    const waves = scheduleWaves([member("deliver", 7), member("land", 3)]);

    assert.deepEqual(waves.waves, [{ number: 1, flows: 1 }, { number: 2, flows: 1 }]);
    assert.equal(waves.waveOf.get("land"), 1);
    assert.equal(waves.waveOf.get("deliver"), 2);
  });

  it("leaves out a member whose pipeline the plan did not resolve, which no row shows", () => {
    const waves = scheduleWaves([member("land", 0), member(null, 0), member(null, 1)]);

    assert.deepEqual(waves.waves, [{ number: 1, flows: 1 }]);
    assert.equal(waves.waveOf.size, 1);
  });

  it("has no waves for a plan with no members", () => {
    assert.deepEqual(scheduleWaves([]), { waveOf: new Map(), waves: [] });
  });
});
