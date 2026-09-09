import { useCallback, useRef } from "react";
import type { Layout, LayoutChangedMeta } from "react-resizable-panels";

/**
 * Persists a resizable panel group's layout in localStorage (react-resizable-panels v4 has no built-in
 * persistence). Only user-driven resizes are saved, so programmatic and initial layouts never clobber
 * the operator's chosen sizes.
 */
export function usePersistentLayout(storageKey: string): {
  defaultLayout: Layout | undefined;
  onLayoutChanged: (layout: Layout, meta: LayoutChangedMeta) => void;
} {
  // Read once per mount: the group applies defaultLayout on mount only, so re-reads are pointless.
  const initial = useRef<Layout | undefined>(undefined);
  const loaded = useRef(false);
  if (!loaded.current) {
    loaded.current = true;
    try {
      const raw = window.localStorage.getItem(storageKey);
      if (raw !== null) {
        const parsed: unknown = JSON.parse(raw);
        if (typeof parsed === "object" && parsed !== null
          && Object.values(parsed).every((size) => typeof size === "number")) {
          initial.current = parsed as Layout;
        }
      }
    } catch {
      // A corrupt store just means the default sizes apply.
      initial.current = undefined;
    }
  }

  const onLayoutChanged = useCallback((layout: Layout, meta: LayoutChangedMeta) => {
    if (meta.isUserInteraction) {
      window.localStorage.setItem(storageKey, JSON.stringify(layout));
    }
  }, [storageKey]);

  return { defaultLayout: initial.current, onLayoutChanged };
}
