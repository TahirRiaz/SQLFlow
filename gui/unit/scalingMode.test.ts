// How the Nodes page says workers start: through a launcher the control plane drives, by an autoscaler reading each
// pool's target, or not at all while the control plane runs runs on its own node.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { WorkerPool } from "../src/api/types.ts";
import { scalingModeExplanation, scalingModeLabel, scalingModeOf } from "../src/features/nodes/scalingMode.ts";

function pool(overrides: Partial<WorkerPool>): WorkerPool {
  return {
    pool: "", minReplicas: 0, manualReplicas: 0, manualUntilUtc: null, manualActive: false, queuedRuns: 0,
    replicaTarget: 0, onlineNodes: 0, updatedUtc: null, updatedBy: null, eligibleQueuedRuns: 0, busyNodes: 0,
    runSlotsPerNode: 4, managedBy: null, managed: null, builtInNode: null, ...overrides,
  };
}

describe("scalingModeOf", () => {
  it("reads managed workers from any pool a launcher manages, ahead of a built-in node", () => {
    const mode = scalingModeOf([
      pool({ builtInNode: { name: "cp-node", runSlots: 4 } }),
      pool({ pool: "etl", managedBy: "process" }),
    ]);
    assert.deepEqual(mode, { kind: "managed", launcher: "process" });
    assert.equal(scalingModeLabel(mode), "managed");
    assert.match(scalingModeExplanation(mode), /through the process launcher/);
  });

  it("reads a built-in node when nothing manages the pools, and says nothing starts workers", () => {
    const mode = scalingModeOf([pool({ builtInNode: { name: "cp-node", runSlots: 6 } })]);
    assert.deepEqual(mode, { kind: "built-in", node: { name: "cp-node", runSlots: 6 } });
    assert.equal(scalingModeLabel(mode), "built-in node");
    assert.match(scalingModeExplanation(mode), /starts no worker/);
    assert.match(scalingModeExplanation(mode), /cp-node, 6 at a time/);
  });

  it("reads an autoscaler when the pools are neither managed nor served by a built-in node, as a deployment's are", () => {
    for (const pools of [[], [pool({}), pool({ pool: "etl" })]]) {
      const mode = scalingModeOf(pools);
      assert.deepEqual(mode, { kind: "autoscaler" });
      assert.equal(scalingModeLabel(mode), "autoscaler");
      assert.match(scalingModeExplanation(mode), /An autoscaler runs each pool at its target/);
    }
  });
});
