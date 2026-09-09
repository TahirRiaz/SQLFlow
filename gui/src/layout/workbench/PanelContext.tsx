import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

export interface PanelContent {
  /** Stable identity so a feature can tell whether the panel currently shows its content. */
  id: string;
  title: string;
  node: ReactNode;
}

interface PanelValue {
  content: PanelContent | null;
  open: (content: PanelContent) => void;
  close: () => void;
}

const PanelContext = createContext<PanelValue | null>(null);

/**
 * The workbench's bottom panel (the VS Code "Panel"): features open live surfaces here (a run's streaming
 * trace, say) without leaving their page. One panel, one content at a time; opening replaces what is shown.
 */
export function PanelProvider({ children }: { children: ReactNode }) {
  const [content, setContent] = useState<PanelContent | null>(null);

  const open = useCallback((next: PanelContent) => {
    setContent(next);
  }, []);

  const close = useCallback(() => {
    setContent(null);
  }, []);

  const value = useMemo<PanelValue>(() => ({ content, open, close }), [content, open, close]);

  return <PanelContext.Provider value={value}>{children}</PanelContext.Provider>;
}

export function usePanel(): PanelValue {
  const context = useContext(PanelContext);
  if (!context) {
    throw new Error("usePanel must be used inside PanelProvider.");
  }

  return context;
}
