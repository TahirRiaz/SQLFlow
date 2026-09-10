import { useSyncExternalStore } from "react";
import { rateLimitPausedUntil, subscribeRateLimit } from "../api/client";

/**
 * The polling cadence for live views on a poll-only API with a 120/min budget:
 * - while the server has rate limited us, polling stops entirely until the pause lapses;
 * - a hidden tab polls at most every 30s (nobody is watching; keep the budget for visible tabs);
 * - otherwise the page's base interval applies.
 * Pass the result as TanStack Query's refetchInterval.
 */
export function pollingInterval(baseMs: number): () => number | false {
  return () => {
    if (rateLimitPausedUntil() !== null) {
      return false;
    }

    return document.visibilityState === "hidden" ? Math.max(baseMs * 6, 30_000) : baseMs;
  };
}

/** Reactive view of the rate-limit pause, for the "rate limited, resuming shortly" banner. */
export function useRateLimitPause(): number | null {
  return useSyncExternalStore(
    (onStoreChange) => subscribeRateLimit(() => onStoreChange()),
    () => rateLimitPausedUntil(),
  );
}
