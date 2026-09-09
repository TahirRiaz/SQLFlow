import { useCallback, useEffect, useRef, useState } from "react";

/**
 * A drop-in replacement for useState whose value is remembered in localStorage under `key`, so a
 * filter or toggle the operator set survives navigating away and reloading the page. The stored value
 * seeds the initial state on mount; every subsequent change is written back.
 *
 * Values are JSON-serialised, so any JSON-safe type works (strings, numbers, booleans, arrays). A
 * corrupt or absent entry falls back to `defaultValue`, and any storage error (private mode, quota)
 * is swallowed so persistence never breaks the page.
 *
 * Keys are namespaced by the caller (for example "sqlflow.filters.pipelines.repo") so distinct pages
 * never collide.
 */
export function useLocalStorageState<T>(
  key: string,
  defaultValue: T | (() => T),
): [T, (next: T | ((prev: T) => T)) => void] {
  const resolveDefault = useCallback(
    () => (typeof defaultValue === "function" ? (defaultValue as () => T)() : defaultValue),
    // defaultValue is only ever read to seed the initial state, so it is intentionally not a dependency.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  const [value, setValue] = useState<T>(() => {
    try {
      const raw = window.localStorage.getItem(key);
      if (raw !== null) {
        return JSON.parse(raw) as T;
      }
    } catch {
      // A corrupt or unreadable entry just means the default applies.
    }
    return resolveDefault();
  });

  // Persist on change. The first run re-writes the loaded value harmlessly, keeping the entry fresh.
  useEffect(() => {
    try {
      window.localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // Storage being unavailable (private mode, quota) must not break the page; the value still lives in memory.
    }
  }, [key, value]);

  return [value, setValue];
}

/**
 * Reads a persisted value once, without subscribing a component to it. Useful for seeding a second
 * piece of state (such as a debounced mirror of a text box) from the same key an input persists to,
 * so both start from the remembered value with no render flash.
 */
export function readLocalStorageState<T>(key: string, defaultValue: T): T {
  try {
    const raw = window.localStorage.getItem(key);
    if (raw !== null) {
      return JSON.parse(raw) as T;
    }
  } catch {
    // Fall through to the default.
  }
  return defaultValue;
}

/**
 * Applies a URL query parameter as a one-time override of a persisted filter: deep links (for example
 * /runs?status=failed or /lineage?name=dbo.Orders) should win over, and update, the remembered value.
 * Pass the current search-param value and the state setter; when the param is present and non-empty on
 * mount it is written into state (and thus persisted). Absent params leave the remembered value intact.
 */
export function useUrlSeed(paramValue: string | null, setter: (next: string) => void): void {
  const applied = useRef(false);
  useEffect(() => {
    if (applied.current) return;
    applied.current = true;
    if (paramValue !== null && paramValue !== "") {
      setter(paramValue);
    }
  }, [paramValue, setter]);
}
