import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { routeTitle } from "../nav";

export interface WorkbenchTab {
  /** The tab's identity: one tab per pathname (query-string changes update the same tab). */
  path: string;
  /** The full URL (path + search) last seen for this tab, restored on activation. */
  url: string;
  title: string;
}

interface TabsValue {
  tabs: WorkbenchTab[];
  /** The pathname of the routed page, i.e. the active tab. */
  activePath: string;
  activate: (tab: WorkbenchTab) => void;
  close: (path: string) => void;
  /** Closes every tab except the given one. */
  closeOthers: (path: string) => void;
  /** Closes every tab sitting to the left of the given one. */
  closeToLeft: (path: string) => void;
  /** Closes every tab sitting to the right of the given one. */
  closeToRight: (path: string) => void;
  closeAll: () => void;
  setTitle: (path: string, title: string) => void;
}

const HOME_PATH = "/";

const TabsContext = createContext<TabsValue | null>(null);

const STORAGE_KEY = "sqlflow.workbench.tabs";

function loadStoredTabs(): WorkbenchTab[] {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return [];
    }

    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.filter((entry): entry is WorkbenchTab =>
      typeof entry === "object" && entry !== null
      && typeof (entry as WorkbenchTab).path === "string"
      && typeof (entry as WorkbenchTab).url === "string"
      && typeof (entry as WorkbenchTab).title === "string");
  } catch {
    // A corrupt store (manual edit, quota weirdness) just means starting with no restored tabs.
    return [];
  }
}

/**
 * VS Code-style editor tabs over the router: every visited route opens (or re-activates) a tab, closing a tab
 * navigates to its neighbor, and the set survives a refresh via sessionStorage. Detail pages refine their
 * tab's title through useTabTitle once they know the entity they show.
 */
export function TabsProvider({ children }: { children: ReactNode }) {
  const location = useLocation();
  const navigate = useNavigate();
  const [tabs, setTabs] = useState<WorkbenchTab[]>(loadStoredTabs);

  // The routed location is the source of truth: it opens new tabs and refreshes the active tab's URL.
  useEffect(() => {
    const path = location.pathname;
    const url = path + location.search;
    setTabs((current) => {
      const index = current.findIndex((tab) => tab.path === path);
      if (index >= 0) {
        if (current[index].url === url) {
          return current;
        }

        const next = [...current];
        next[index] = { ...next[index], url };
        return next;
      }

      return [...current, { path, url, title: routeTitle(path).title }];
    });
  }, [location.pathname, location.search]);

  useEffect(() => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(tabs));
  }, [tabs]);

  const activate = useCallback((tab: WorkbenchTab) => {
    navigate(tab.url);
  }, [navigate]);

  /**
   * The one closing path: drops every tab the predicate selects, then keeps the routed page valid. Closing
   * the active tab focuses the nearest survivor to its right, else the one to its left; emptying the strip
   * falls back to the dashboard, so there is never a routed page without a tab.
   */
  const closeTabs = useCallback((shouldClose: (tab: WorkbenchTab, index: number) => boolean) => {
    const survivors = tabs.filter((tab, index) => !shouldClose(tab, index));
    if (survivors.length === tabs.length) {
      return;
    }

    if (survivors.length === 0) {
      const home: WorkbenchTab = { path: HOME_PATH, url: HOME_PATH, title: routeTitle(HOME_PATH).title };
      setTabs([home]);
      navigate(home.url);
      return;
    }

    setTabs(survivors);

    const activeIndex = tabs.findIndex((tab) => tab.path === location.pathname);
    if (activeIndex < 0 || !shouldClose(tabs[activeIndex], activeIndex)) {
      return;
    }

    const survivorsBefore = tabs
      .slice(0, activeIndex)
      .filter((tab, index) => !shouldClose(tab, index))
      .length;
    navigate(survivors[Math.min(survivorsBefore, survivors.length - 1)].url);
  }, [tabs, location.pathname, navigate]);

  const close = useCallback((path: string) => {
    closeTabs((tab) => tab.path === path);
  }, [closeTabs]);

  const closeOthers = useCallback((path: string) => {
    closeTabs((tab) => tab.path !== path);
  }, [closeTabs]);

  const closeToLeft = useCallback((path: string) => {
    const index = tabs.findIndex((tab) => tab.path === path);
    if (index < 0) {
      return;
    }

    closeTabs((_tab, position) => position < index);
  }, [tabs, closeTabs]);

  const closeToRight = useCallback((path: string) => {
    const index = tabs.findIndex((tab) => tab.path === path);
    if (index < 0) {
      return;
    }

    closeTabs((_tab, position) => position > index);
  }, [tabs, closeTabs]);

  const closeAll = useCallback(() => {
    closeTabs(() => true);
  }, [closeTabs]);

  const setTitle = useCallback((path: string, title: string) => {
    setTabs((current) => {
      const index = current.findIndex((tab) => tab.path === path);
      if (index < 0 || current[index].title === title) {
        return current;
      }

      const next = [...current];
      next[index] = { ...next[index], title };
      return next;
    });
  }, []);

  const value = useMemo<TabsValue>(() => ({
    tabs,
    activePath: location.pathname,
    activate,
    close,
    closeOthers,
    closeToLeft,
    closeToRight,
    closeAll,
    setTitle,
  }), [tabs, location.pathname, activate, close, closeOthers, closeToLeft, closeToRight, closeAll, setTitle]);

  return <TabsContext.Provider value={value}>{children}</TabsContext.Provider>;
}

export function useWorkbenchTabs(): TabsValue {
  const context = useContext(TabsContext);
  if (!context) {
    throw new Error("useWorkbenchTabs must be used inside TabsProvider.");
  }

  return context;
}

/** Reported by detail pages once their data loads, so the tab reads "orders_ods" instead of "Pipeline". */
export function useTabTitle(title: string | undefined): void {
  const { setTitle } = useWorkbenchTabs();
  const { pathname } = useLocation();

  useEffect(() => {
    if (title !== undefined && title !== "") {
      setTitle(pathname, title);
    }
  }, [title, pathname, setTitle]);
}
