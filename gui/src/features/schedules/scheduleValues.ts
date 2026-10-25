import type { ResolvedRunValue, RunValue, RunValueOrigin, ScheduleMemberParameters } from "../../api/types.ts";
import { runValueAssignment } from "../../lib/runValues.ts";

// What a schedule gives each flow it runs (ScheduleParameters from GET /schedules/{id}/parameters), in the words the
// Schedules page and the Run now dialog show it: where each value comes from, a member's values as one line, and whether
// a member's run would be refused. The control plane reads the preview as a fire applies the schedule; these helpers only
// say it.

/** Where a value a run takes comes from, as a short phrase. */
export function originLabel(origin: RunValueOrigin): string {
  switch (origin) {
    case "Given":
      return "from the schedule";
    case "Default":
      return "the flow's default";
    case "Unset":
      return "not set";
    case "Missing":
      return "missing: required";
    case "NotDeclared":
      return "not declared by the flow";
    default:
      return "from the schedule, for the kind";
  }
}

/** A run value as text: a list's values separated by commas; a hyphen for none. */
export function valueText(value: RunValue | null): string {
  if (value === null) {
    return "-";
  }

  return typeof value === "string" ? value : value.join(", ");
}

/** The values a member's run takes that a line shows: every one but a parameter it takes no value for. */
export function shownValues(member: ScheduleMemberParameters): ResolvedRunValue[] {
  return member.values.filter((value) => value.origin !== "Unset");
}

/** A member's values as one line: `logSource=STAT_COMP`, a default marked `(default)`, a missing one `?`. */
export function memberValuesLine(member: ScheduleMemberParameters): string {
  const parts = shownValues(member).map((value) => {
    switch (value.origin) {
      case "Default":
        return `${value.name}=${valueText(value.value)} (default)`;
      case "Missing":
        return `${value.name}=?`;
      default:
        return `${value.name}=${valueText(value.value)}`;
    }
  });
  return parts.length === 0 ? "no parameters" : parts.join(" · ");
}

/** How many of the members' runs would be refused, as the preview reads them. */
export function refusedMembers(members: readonly ScheduleMemberParameters[]): number {
  return members.filter((member) => member.problems.length > 0).length;
}

/** A schedule's own values as assignments, a list in brackets: `logSource=STAT_COMP`, `region=[north, south]`. */
export function scheduleValueAssignments(values: Readonly<Record<string, RunValue>>): string[] {
  return Object.entries(values)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([name, value]) => runValueAssignment(name, value));
}
