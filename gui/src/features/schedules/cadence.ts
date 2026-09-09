/**
 * Cadence in words. A schedules list is scanned, not parsed: "Daily 22:10" answers "when does this run" at a
 * glance where `10 22 * * *` makes the reader decode five positional fields per row. The raw expression stays
 * one hover away (the trigger tooltip), so nothing is lost.
 *
 * Every function here returns null rather than guessing. Cron is a large grammar and a WRONG summary is far
 * worse than none: an operator who reads "Daily 22:10" over an expression that actually fires twelve times a
 * day has been misinformed by the UI. Only the shapes below are claimed; everything else falls back to the
 * expression itself.
 */

const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/** A cron day-of-week token as an index into {@link DAY_NAMES}: 0-7 (both 0 and 7 are Sunday) or a name. */
function dayIndex(token: string): number | null {
  if (/^\d$/.test(token)) {
    const value = Number.parseInt(token, 10);
    return value === 7 ? 0 : value <= 6 ? value : null;
  }

  const named = DAY_NAMES.findIndex((day) => day.toLowerCase() === token.slice(0, 3).toLowerCase());
  return named === -1 ? null : named;
}

/** "1-5" as "Mon-Fri", "1,3,5" as "Mon, Wed, Fri", "1" as "Mon". Null for step or wildcard forms. */
function dayList(field: string): string | null {
  const range = /^([^-]+)-([^-]+)$/.exec(field);
  if (range) {
    const from = dayIndex(range[1]);
    const to = dayIndex(range[2]);
    return from === null || to === null ? null : `${DAY_NAMES[from]}-${DAY_NAMES[to]}`;
  }

  const days = field.split(",").map(dayIndex);
  return days.some((day) => day === null) ? null : days.map((day) => DAY_NAMES[day!]).join(", ");
}

/** A minute/hour field pair as a zero-padded wall clock, or null when either is not a plain in-range number. */
function wallClock(minute: string, hour: string): string | null {
  if (!/^\d{1,2}$/.test(minute) || !/^\d{1,2}$/.test(hour)) {
    return null;
  }

  const m = Number.parseInt(minute, 10);
  const h = Number.parseInt(hour, 10);
  if (m > 59 || h > 23) {
    return null;
  }

  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

function ordinal(day: number): string {
  const suffix = day % 100 >= 11 && day % 100 <= 13 ? "th"
    : day % 10 === 1 ? "st"
    : day % 10 === 2 ? "nd"
    : day % 10 === 3 ? "rd"
    : "th";
  return `${day}${suffix}`;
}

/**
 * A five-field cron expression in words, or null when it is not one of the recognised shapes and the caller
 * should show the expression itself.
 */
export function cronSummary(cron: string): string | null {
  const fields = cron.trim().split(/\s+/);
  if (fields.length !== 5) {
    return null;
  }

  const [minute, hour, dayOfMonth, month, dayOfWeek] = fields;
  const clock = wallClock(minute, hour);
  const everyDate = dayOfMonth === "*" && month === "*";

  if (clock !== null && everyDate) {
    if (dayOfWeek === "*") {
      return `Daily ${clock}`;
    }
    const days = dayList(dayOfWeek);
    return days === null ? null : `${days} ${clock}`;
  }

  if (clock !== null && month === "*" && dayOfWeek === "*" && /^\d{1,2}$/.test(dayOfMonth)) {
    const day = Number.parseInt(dayOfMonth, 10);
    return day >= 1 && day <= 31 ? `Monthly on the ${ordinal(day)}, ${clock}` : null;
  }

  const everything = hour === "*" && everyDate && dayOfWeek === "*";
  if (everything && /^\d{1,2}$/.test(minute) && Number.parseInt(minute, 10) <= 59) {
    return `Hourly at :${minute.padStart(2, "0")}`;
  }

  const stepMinutes = /^\*\/(\d{1,2})$/.exec(minute);
  if (everything && stepMinutes !== null) {
    return `Every ${stepMinutes[1]} min`;
  }

  return null;
}

/** An interval trigger in words. Whole hours and whole minutes read as such; anything else stays in seconds. */
export function intervalSummary(seconds: number): string {
  if (seconds >= 3600 && seconds % 3600 === 0) {
    const hours = seconds / 3600;
    return hours === 1 ? "Hourly" : `Every ${hours} hours`;
  }

  if (seconds >= 60 && seconds % 60 === 0) {
    const minutes = seconds / 60;
    return minutes === 1 ? "Every minute" : `Every ${minutes} min`;
  }

  return `Every ${seconds}s`;
}

/**
 * An IANA zone shortened to the part that distinguishes it: "Europe/Oslo" to "Oslo". A schedules list is
 * almost always one estate in one zone, so the region prefix is the same on every row and carries no
 * information; the full identifier stays in the tooltip.
 */
export function shortZone(timezone: string): string {
  const city = timezone.split("/").pop() ?? timezone;
  return city.replace(/_/g, " ");
}
