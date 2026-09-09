import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";

/** The run groups the dock is watching, persisted so a launched run survives a tab switch or a full reload: the
 * whole point is that closing the pre-flight sheet (or navigating away) never strands a running execution. */
const GROUPS_KEY = "sqlflow.rundock.groups";
/** Whether the dock is collapsed to its single pill. Persisted so the operator's choice sticks across reloads. */
const MINIMIZED_KEY = "sqlflow.rundock.minimized";
/** A cap so a long session cannot grow the tracked list without bound; the oldest tracked group falls off first. */
const MAX_TRACKED = 12;

export interface RunDockApi {
  /** The tracked group ids, oldest first. */
  groupIds: string[];
  minimized: boolean;
  /** Start (or keep) watching a run group and pop the dock open, so a freshly launched run is immediately visible. */
  track: (groupId: string) => void;
  /** Stop watching a group (the user dismissed its chip, or it 404'd / finished and auto-expired). */
  untrack: (groupId: string) => void;
  setMinimized: (minimized: boolean) => void;
}

const RunDockContext = createContext<RunDockApi | null>(null);

/** Reads the persisted id list defensively: a corrupt or hand-edited value yields an empty list, never a throw. */
function readGroups(): string[] {
  try {
    const raw = window.localStorage.getItem(GROUPS_KEY);
    if (raw === null) {
      return [];
    }

    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((value): value is string => typeof value === "string") : [];
  } catch {
    return [];
  }
}

/**
 * The run dock's shared state. It holds the ids of the run groups the operator launched this session and whether the
 * dock is collapsed, both mirrored to localStorage so a run stays reachable after the launching sheet closes, the tab
 * changes, or the page reloads. The `storage` listener keeps two open tabs in agreement. The dock UI (RunDock) and the
 * launch sites (the schedule run board, the trigger-run dialog, a group re-run) are the only consumers.
 */
export function RunDockProvider({ children }: { children: ReactNode }) {
  const [groupIds, setGroupIds] = useState<string[]>(readGroups);
  const [minimized, setMinimizedState] = useState<boolean>(() => window.localStorage.getItem(MINIMIZED_KEY) === "1");

  useEffect(() => {
    window.localStorage.setItem(GROUPS_KEY, JSON.stringify(groupIds));
  }, [groupIds]);

  const track = useCallback((groupId: string) => {
    setGroupIds((prev) => {
      if (prev.includes(groupId)) {
        return prev;
      }

      const next = [...prev, groupId];
      return next.length > MAX_TRACKED ? next.slice(next.length - MAX_TRACKED) : next;
    });
    setMinimizedState(false);
    window.localStorage.setItem(MINIMIZED_KEY, "0");
  }, []);

  const untrack = useCallback((groupId: string) => {
    setGroupIds((prev) => prev.filter((id) => id !== groupId));
  }, []);

  const setMinimized = useCallback((value: boolean) => {
    setMinimizedState(value);
    window.localStorage.setItem(MINIMIZED_KEY, value ? "1" : "0");
  }, []);

  // Another tab tracked or dismissed a run: mirror its list so both windows show the same dock.
  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key === GROUPS_KEY) {
        setGroupIds(readGroups());
      } else if (event.key === MINIMIZED_KEY) {
        setMinimizedState(event.newValue === "1");
      }
    };

    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, []);

  const api = useMemo<RunDockApi>(
    () => ({ groupIds, minimized, track, untrack, setMinimized }),
    [groupIds, minimized, track, untrack, setMinimized],
  );

  return <RunDockContext.Provider value={api}>{children}</RunDockContext.Provider>;
}

export function useRunDock(): RunDockApi {
  const ctx = useContext(RunDockContext);
  if (ctx === null) {
    throw new Error("useRunDock must be used inside <RunDockProvider>");
  }

  return ctx;
}
