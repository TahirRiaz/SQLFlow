import { useSyncExternalStore } from "react";

// One clock for the whole page: every component counting up (how long a run has been executing) reads the same instant,
// and a single timer ticks while any of them is mounted and active, so a board of running rows sets one interval, not
// one per cell. Reading the time through an external store keeps render pure: the instant is read in the timer and at
// subscription, never while rendering.

const TICK_MS = 1000;
const listeners = new Set<() => void>();
let current = Date.now();
let timer: number | null = null;

function tick(): void {
  current = Date.now();
  for (const listener of listeners) {
    listener();
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (timer === null) {
    // The clock was idle and its instant is stale: bring it up to date now, which React notices after subscribing and
    // renders again, rather than showing the stale instant until the first tick.
    current = Date.now();
    timer = window.setInterval(tick, TICK_MS);
  }

  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer !== null) {
      window.clearInterval(timer);
      timer = null;
    }
  };
}

function subscribeIdle(): () => void {
  return () => {};
}

function snapshot(): number {
  return current;
}

/** The current time in epoch milliseconds, ticking every second while `active`; idle (no timer) when not. */
export function useNow(active: boolean): number {
  return useSyncExternalStore(active ? subscribe : subscribeIdle, snapshot, snapshot);
}
