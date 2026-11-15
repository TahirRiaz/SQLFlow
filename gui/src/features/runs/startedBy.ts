import type { RunStartedBy, RunStartedByKind } from "../../api/types.ts";

// What started a run, as a row of the runs board shows it. The control plane decides what started a run and words the
// whole answer (RunStartedBy.summary, the row's hover); a row has room for who or what in a few words: the schedule, the
// person, the trigger or the command-line account, then a quiet qualifier saying how.

/** A row's face of what started a run: the name it leads with, and a quiet qualifier after it (or none). */
export interface StartedByFace {
  primary: string;
  secondary: string | null;
}

/** What a row of the runs board shows for what started a run. */
export function startedByFace(startedBy: RunStartedBy): StartedByFace {
  switch (startedBy.kind) {
    case "schedule":
      return { primary: startedBy.scheduleName ?? "a schedule", secondary: "on its own" };
    case "chain":
      return {
        primary: startedBy.scheduleName ?? "a schedule",
        secondary: startedBy.afterScheduleName === null ? "after its parents" : `after ${startedBy.afterScheduleName}`,
      };
    case "user":
      return {
        primary: startedBy.who ?? "unnamed caller",
        secondary: startedBy.scheduleName === null ? null : `ran ${startedBy.scheduleName}`,
      };
    case "trigger":
      return {
        primary: startedBy.who ?? "external trigger",
        secondary: startedBy.scheduleName === null ? "trigger" : `fired ${startedBy.scheduleName}`,
      };
    case "cli":
      return { primary: startedBy.who ?? "command line", secondary: startedBy.who === null ? null : "command line" };
    case "unknown":
      return { primary: "not recorded", secondary: null };
  }
}

/** The runs board's "Started by" filter: each choice named by the runs it keeps, in the order a reader looks for them. */
export const startedByFilterOptions: readonly { value: RunStartedByKind; label: string }[] = [
  { value: "schedule", label: "Schedule, on its own" },
  { value: "chain", label: "Schedule, after parents" },
  { value: "user", label: "Person or API client" },
  { value: "trigger", label: "External trigger" },
  { value: "cli", label: "Command line" },
  { value: "unknown", label: "Not recorded" },
];

/** The filter's choice that keeps every run, worded as its trigger shows it. */
export const startedByAnyLabel = "Started by: any";

/** Whether a value read back from the address or storage is a kind the filter offers. */
export function isStartedByKind(value: string): value is RunStartedByKind {
  return startedByFilterOptions.some((option) => option.value === value);
}
