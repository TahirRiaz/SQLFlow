import type { BuiltInNode, WorkerPool } from "../../api/types";

/** How this estate gets its workers, as the pools the control plane reports say: the control plane starts them through a
 *  launcher (managed workers), an autoscaler reads each pool's target, or the control plane runs runs on its own node and
 *  nothing it can see starts a worker. */
export type ScalingMode =
  | { kind: "managed"; launcher: string }
  | { kind: "autoscaler" }
  | { kind: "built-in"; node: BuiltInNode };

export function scalingModeOf(pools: readonly WorkerPool[]): ScalingMode {
  const launcher = pools.find((pool) => pool.managedBy !== null)?.managedBy ?? null;
  if (launcher !== null) {
    return { kind: "managed", launcher };
  }

  const node = pools.find((pool) => pool.builtInNode !== null)?.builtInNode ?? null;
  return node !== null ? { kind: "built-in", node } : { kind: "autoscaler" };
}

/** The mode in a word or two, for a heading or a summary cell. */
export function scalingModeLabel(mode: ScalingMode): string {
  switch (mode.kind) {
    case "managed":
      return "managed";
    case "built-in":
      return "built-in node";
    default:
      return "autoscaler";
  }
}

/** What the mode means for the pools and their controls, for the explanation behind the mode. */
export function scalingModeExplanation(mode: ScalingMode): string {
  switch (mode.kind) {
    case "managed":
      return `The control plane starts and stops these workers itself, through the ${mode.launcher} launcher. Each managed pool always keeps its minimum running and starts more for queued runs no free slot covers; an idle extra worker leaves after a few minutes, and only once it holds nothing. Always on raises the minimum, and Spawn raises a pool for a window.`;
    case "built-in":
      return `Managed workers are off, so this control plane starts no worker. It runs runs on its own node, ${mode.node.name}, ${mode.node.runSlots} at a time, and answers each pool's target to an autoscaler, if one reads it. Without one, Always on and Spawn are recorded and nothing starts; turn on ControlPlane:ManagedWorkers to have the control plane start and stop workers itself.`;
    default:
      return "An autoscaler runs each pool at its target: the greatest of its demand (the queued runs its workers would start right now, divided by what one worker executes at once, plus the workers already busy), an always-on floor, and any active manual override. The control plane only answers that target. Always on keeps a worker warm, and Spawn scales a pool up for a window or starts one when the pool is at zero.";
  }
}
