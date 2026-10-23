import type { RunValue } from "../api/types";

// A run value is one value or a list of them (RunValue); these read either form, so a page that shows or edits a run's
// values never tells them apart itself.

/** Every value a run value holds, in order: a list's values, or the one value alone; none when it is absent. */
export function runValueItems(value: RunValue | null | undefined): string[] {
  if (value === null || value === undefined) {
    return [];
  }

  return typeof value === "string" ? [value] : [...value];
}

/** A run value as one line: a list's values separated by commas, for a label or a summary. */
export function runValueText(value: RunValue): string {
  return typeof value === "string" ? value : value.join(", ");
}

/** A parameter and its run value as one assignment, a list's values in brackets: `region=north`, `region=[north, south]`. */
export function runValueAssignment(name: string, value: RunValue): string {
  return typeof value === "string" ? `${name}=${value}` : `${name}=[${value.join(", ")}]`;
}

/** The run value some values make: the value itself when there is one, the list when there are several, none when empty. */
export function runValueOf(items: readonly string[]): RunValue | undefined {
  if (items.length === 0) {
    return undefined;
  }

  return items.length === 1 ? items[0] : [...items];
}
