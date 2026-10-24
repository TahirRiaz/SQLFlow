// The Maintenance page's words and sums, apart from anything it draws: what each verdict is called and means, how a size,
// a number of days and an area's cleanup read in the grid, and what the search matches. Kept free of the GUI's components
// so the unit tests read it as it is.

import type { StorageArea, StorageVerdict } from "../../api/types.ts";
import { formatBytes } from "../../lib/time.ts";

/**
 * The verdicts in the order the page reads them, from what may go at no loss, through what may go at a cost, to what is
 * kept: the order of the question "what can I remove?".
 */
export const verdictOrder: StorageVerdict[] = ["safe", "optional", "keep"];

export const verdictLabels: Record<StorageVerdict, string> = {
  safe: "Safe to remove",
  optional: "Optional",
  keep: "Keep",
};

/** Each verdict in a few words: the caption under its tile and beside its group. */
export const verdictShort: Record<StorageVerdict, string> = {
  safe: "nothing anyone needs is lost",
  optional: "removable; each says what is lost",
  keep: "needed; never removed here",
};

/** What each verdict means, in full: the words behind the chip. */
export const verdictMeaning: Record<StorageVerdict, string> = {
  safe: "Removing it loses nothing anyone needs: a superseded copy, a duplicate, or finished work in progress. Most of it cleans itself; the rest you give a limit.",
  optional: "It may be removed, at the cost the area states. Nothing removes it until an admin sets how long it stays.",
  keep: "Needed: it explains a run, a record or an action, holds state something reads, or holds statistics. Nothing set on this page removes it.",
};

/** The heading over an area's reason, which answers a different question for each verdict. */
export const reasonHeading: Record<StorageVerdict, string> = {
  safe: "Why removing it is safe",
  optional: "What removing it costs",
  keep: "Why it is kept",
};

/** A byte count for people, in binary units. */
export function sizeText(bytes: number): string {
  return formatBytes(Math.max(0, bytes));
}

/** "1 day" / "90 days". */
export function daysText(days: number): string {
  return `${days.toLocaleString()} day${days === 1 ? "" : "s"}`;
}

/**
 * What the grid says about how an area's data leaves the database: for an area that takes a limit, the limit and what it
 * would remove now; for one that cleans itself, its rule; for one that is kept, how it leaves at all, and whether it grows
 * with nothing removing it.
 */
export function cleanupSummary(area: StorageArea): { headline: string; detail: string | null; grows: boolean } {
  const retention = area.retention;
  if (retention !== null) {
    if (retention.days === null) {
      return { headline: "No limit set", detail: "Stays until an admin sets one", grows: false };
    }

    const removable = retention.reclaimable === null
      ? retention.reclaimableError === null ? null : "Could not count what would go"
      : retention.reclaimable === 0 ? "Nothing to remove now" : `${retention.reclaimable.toLocaleString()} ${retention.unit} to remove`;
    return { headline: `Older than ${daysText(retention.days)}`, detail: removable, grows: false };
  }

  const brief = area.brief ?? area.lifecycle ?? "";
  if (area.verdict === "safe") {
    return { headline: brief, detail: "Cleans itself", grows: false };
  }

  return { headline: brief, detail: area.grows ? "Grows with use" : null, grows: area.grows };
}

/** The tables an area holds, in a few words: its column, its one table, or how many. */
export function holdingText(area: StorageArea): string {
  if (area.column !== null) {
    return `${area.column.column} of ${area.column.schema}.${area.column.table}`;
  }

  if (area.tables.length === 1) {
    return `${area.tables[0].schema}.${area.tables[0].name}`;
  }

  if (area.tables.length === 0) {
    return area.claims.join(", ");
  }

  return `${area.tables.length.toLocaleString()} tables`;
}

/** Whether an area matches the search: its title, what it holds, its subject, its owner or any of its tables. */
export function matchesSearch(area: StorageArea, search: string): boolean {
  const needle = search.trim().toLowerCase();
  if (needle === "") {
    return true;
  }

  const haystack = [
    area.title, area.holds, area.group, area.owner, area.id,
    ...area.claims, ...area.tables.map((t) => `${t.schema}.${t.name}`),
    area.column === null ? "" : `${area.column.schema}.${area.column.table}.${area.column.column}`,
  ].join("\n").toLowerCase();
  return needle.split(/\s+/).every((word) => haystack.includes(word));
}
