// The trace tray's state: the operation traces the workbench holds while they run, which of them ended unseen, and when
// one leaves. Kept free of React so the rules are tested on their own (unit/traceTrayState.test.ts); TraceTray.tsx renders it.

/** What a trace's view last said about it: whether it has ended, how, and the problems of its current attempt. */
export interface TraceStatus {
  ended: boolean;
  failed: boolean;
  /** Warning and error lines of the current attempt, errors included. */
  problems: number;
  errors: number;
}

/** One trace the tray holds. */
export interface HeldTrace {
  id: string;
  title: string;
  /** Null until the trace's view first reports, which counts as running. */
  status: TraceStatus | null;
  /** The trace ended while it was not shown. Cleared once it is. */
  unseen: boolean;
}

/** The most traces the tray holds; the oldest leaves first when another arrives. */
export const MAX_HELD_TRACES = 6;

/** How the outcome of a trace reads: an error, a warning, or a clean end. */
export type TraceTone = "error" | "warning" | "success";

/** Whether a held trace is still running: it has not reported, or reported that it has not ended. */
export function isRunning(trace: HeldTrace): boolean {
  return trace.status === null || !trace.status.ended;
}

/** The tone an ended trace's outcome reads in; a running one has none. */
export function toneOf(trace: HeldTrace): TraceTone | null {
  const status = trace.status;
  if (status === null || !status.ended) {
    return null;
  }

  if (status.failed || status.errors > 0) {
    return "error";
  }

  return status.problems > 0 ? "warning" : "success";
}

/**
 * Holds a trace an operation just started, or starts a held one again (a repeat sync): a held trace is moved to the
 * newest place with its status and its unseen mark cleared. A trace past the bound pushes the oldest out.
 */
export function holdStarted<T extends HeldTrace>(traces: readonly T[], trace: T): T[] {
  const others = traces.filter((held) => held.id !== trace.id);
  const next = [...others, { ...trace, status: null, unseen: false }];
  return next.length > MAX_HELD_TRACES ? next.slice(next.length - MAX_HELD_TRACES) : next;
}

/**
 * Holds a trace someone asked to see: one the tray already holds is kept as it is, still streaming, so asking for it
 * never restarts it; another is added as {@link holdStarted} adds it.
 */
export function holdAsked<T extends HeldTrace>(traces: readonly T[], trace: T): T[] {
  return traces.some((held) => held.id === trace.id) ? [...traces] : holdStarted(traces, trace);
}

/**
 * Records what a trace's view reports. A trace that ends while it is not shown is marked unseen, so the title bar asks
 * for a look; one that ends in view, or reports again after ending, is not.
 */
export function report<T extends HeldTrace>(
  traces: readonly T[],
  id: string,
  status: TraceStatus,
  shownId: string | null,
): T[] {
  return traces.map((held) => {
    if (held.id !== id) {
      return held;
    }

    const endsNow = isRunning(held) && status.ended;
    const unseen = held.id === shownId ? false : held.unseen || endsNow;
    return { ...held, status, unseen };
  });
}

/** Clears the unseen mark of the trace being shown. */
export function markSeen<T extends HeldTrace>(traces: readonly T[], shownId: string | null): T[] {
  return traces.map((held) => (held.id === shownId && held.unseen ? { ...held, unseen: false } : held));
}

/**
 * Lets go of the traces there is nothing more to hold for: ended, seen, and not shown. A trace someone has read and
 * closed leaves the title bar; one still running, or ended and not yet looked at, stays.
 */
export function retireSeen<T extends HeldTrace>(traces: readonly T[], shownId: string | null): T[] {
  return traces.filter((held) => held.id === shownId || isRunning(held) || held.unseen);
}

/** What the title bar draws for the traces held: how many run, and the tone that most needs a look. */
export interface TrayDigest {
  running: number;
  /** The worst tone among the traces that ended unseen; null when none did. */
  attention: TraceTone | null;
}

/** How much each tone asks for a look, so the worst of several unseen outcomes is the one shown. */
const URGENCY: Record<TraceTone, number> = { success: 0, warning: 1, error: 2 };

/** Sums the held traces up for the title bar's indicator. */
export function digest(traces: readonly HeldTrace[]): TrayDigest {
  let running = 0;
  let attention: TraceTone | null = null;
  for (const trace of traces) {
    if (isRunning(trace)) {
      running += 1;
    }

    const tone = trace.unseen ? toneOf(trace) : null;
    if (tone !== null && (attention === null || URGENCY[tone] > URGENCY[attention])) {
      attention = tone;
    }
  }

  return { running, attention };
}
