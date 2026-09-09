import { lazy, type ComponentType, type LazyExoticComponent } from "react";

// A tab left open across a deploy holds a module graph whose content-hashed chunks no longer exist: nginx serves
// index.html with no-cache, but /assets/* from the previous image ships out with it, so the first navigation to a
// route this document never loaded fails its dynamic import with a 404. Reloading is the fix, because the fresh
// document names the chunks that exist now. The session marker bounds that to one reload per route: if the import
// fails again from a fresh document then a stale tab was not the cause, and the error has to surface rather than
// drive a reload loop.

const MARKER_PREFIX = "sqlflow.chunk-reload.";

function sessionStore(): Storage | null {
  // The property access itself throws where storage is walled off (sandboxed frame, blocked cookies).
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

function clearReloadMarker(name: string): void {
  const store = sessionStore();
  if (!store) {
    return;
  }
  try {
    store.removeItem(MARKER_PREFIX + name);
  } catch {
    // A store that rejects reads and writes cannot be holding a marker to clear.
  }
}

/** Takes the one reload this document is allowed for `name`, or returns false if the failure must be surfaced. */
function claimReload(name: string): boolean {
  // Being offline is the other reason a chunk will not load, and reloading then costs the whole app rather than
  // the one route, so let the caller surface it instead.
  if (!navigator.onLine) {
    return false;
  }
  const store = sessionStore();
  if (!store) {
    // Without somewhere to record the attempt the reload cannot be bounded, so it must not be started.
    return false;
  }
  const key = MARKER_PREFIX + name;
  try {
    if (store.getItem(key) !== null) {
      store.removeItem(key);
      return false;
    }
    store.setItem(key, "1");
    return true;
  } catch {
    return false;
  }
}

/** lazy() for route components, recovering from the stale-chunk 404 a tab hits when it survives a deploy. */
export function lazyRoute<T extends ComponentType<any>>(
  name: string,
  factory: () => Promise<{ default: T }>,
): LazyExoticComponent<T> {
  return lazy(async () => {
    try {
      const module = await factory();
      // This document can reach its chunks, so leave the next deploy a reload to spend.
      clearReloadMarker(name);
      return module;
    } catch (error) {
      if (!claimReload(name)) {
        throw error;
      }
      window.location.reload();
      // reload() only schedules the navigation. Never settling holds the Suspense fallback for the document's
      // remaining life, instead of flashing an error the user has no way to act on.
      return new Promise<never>(() => {});
    }
  });
}
