import type { SchedulePlanMember } from "../../api/types.ts";

/** One wave of a schedule's fire as a filter offers it: its number as the run board shows it, and how many flows run in it. */
export interface ScheduleWave {
  number: number;
  flows: number;
}

/** A schedule's fire in waves, read from its plan: the wave each member flow runs in, by pipeline id, and the waves. */
export interface ScheduleWaves {
  waveOf: ReadonlyMap<string, number>;
  waves: readonly ScheduleWave[];
}

/**
 * Numbers a schedule's fire the way its run board does: the plan's waves in order, the first one wave 1. A flow's
 * lineage wave counts every flow of its repository, so it is not the order a fire runs it in; this is. A member whose
 * pipeline the plan did not resolve has no row on a pipelines list, so it is not counted, and a member the fire passes
 * over (deactivated, manual or disabled) is not in the plan at all.
 */
export function scheduleWaves(members: readonly SchedulePlanMember[]): ScheduleWaves {
  const planWaves = [...new Set(members.map((member) => member.wave))].sort((a, b) => a - b);
  const numberOf = new Map(planWaves.map((wave, index) => [wave, index + 1]));
  const waveOf = new Map<string, number>();
  const flows = new Map<number, number>();
  for (const member of members) {
    if (member.pipelineId === null) {
      continue;
    }

    const number = numberOf.get(member.wave) as number;
    waveOf.set(member.pipelineId, number);
    flows.set(number, (flows.get(number) ?? 0) + 1);
  }

  return {
    waveOf,
    waves: [...flows.entries()].sort(([a], [b]) => a - b).map(([number, count]) => ({ number, flows: count })),
  };
}
