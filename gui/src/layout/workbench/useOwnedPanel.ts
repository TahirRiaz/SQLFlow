import { useCallback, useEffect, useRef, type ReactNode } from "react";
import { usePanel } from "./PanelContext";

/**
 * The bottom panel as one feature surface owns it: content ids carry the feature's `prefix`, so the surface can
 * tell whether the panel is showing its content (and not a run trace someone opened meanwhile), re-open it with
 * fresh data while it is, and close it when the surface unmounts, so a tab switch or a navigation never leaves a
 * detail from a page that is no longer there.
 */
export function useOwnedPanel(prefix: string): {
  /** The id of the content the panel shows, without the prefix, when it is this surface's; null otherwise. */
  ownedId: string | null;
  show: (id: string, title: string, node: ReactNode) => void;
  close: () => void;
} {
  const { content, open, close } = usePanel();
  const ownedId = content !== null && content.id.startsWith(prefix) ? content.id.slice(prefix.length) : null;

  const owned = useRef(ownedId);
  useEffect(() => {
    owned.current = ownedId;
  }, [ownedId]);
  useEffect(() => () => {
    if (owned.current !== null) {
      close();
    }
  }, [close]);

  const show = useCallback(
    (id: string, title: string, node: ReactNode) => open({ id: prefix + id, title, node }),
    [open, prefix],
  );

  return { ownedId, show, close };
}
