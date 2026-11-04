import { useCallback, useMemo, useState } from "react";
import { Outlet, useLocation } from "react-router-dom";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { RunDock } from "../features/runs/RunDock";
import { ExplainDrawer, ExplainProvider } from "../features/chat/ExplainContext";
import { RepoFileDrawer, RepoFileWindowProvider } from "../features/repos/RepoFileWindow";
import { RunDockProvider } from "../features/runs/RunDockContext";
import { sideBarClosedOnFirstVisit } from "../modules/registry";
import { groupForPath, selectedNavPath, type NavGroup, type NavItem } from "./nav";
import { ActivityBar } from "./workbench/ActivityBar";
import { CommandPalette } from "./workbench/CommandPalette";
import { PanelHost } from "./workbench/PanelHost";
import { PanelProvider, usePanel } from "./workbench/PanelContext";
import { SideBar, type SideBarReveal } from "./workbench/SideBar";
import { SideBarRoomContext, type SideBarRoom } from "./workbench/SideBarRoom";
import { sideBarStartsOpen } from "./workbench/sideBarStart";
import { StatusBar } from "./workbench/StatusBar";
import { TabsBar } from "./workbench/TabsBar";
import { TabsProvider } from "./workbench/TabsContext";
import { TitleBar } from "./workbench/TitleBar";
import { TraceTrayProvider } from "./workbench/TraceTray";
import { useMenuNavigate } from "./workbench/useMenuNavigate";
import { usePersistentLayout } from "./workbench/usePersistentLayout";

const SIDEBAR_KEY = "sqlflow.workbench.sidebar";

/**
 * The workbench frame (DESIGN.md section 6): title bar over activity bar + resizable side bar +
 * (tab strip, editor, bottom panel) over the status bar. Only the editor and panel scroll.
 */
function WorkbenchFrame() {
  const location = useLocation();
  const [sidebarOpen, setSidebarOpen] = useState(
    () => sideBarStartsOpen(window.localStorage.getItem(SIDEBAR_KEY), sideBarClosedOnFirstVisit()),
  );
  const [reveal, setReveal] = useState<SideBarReveal | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const { content: panelContent } = usePanel();
  const navigate = useMenuNavigate();
  const horizontalLayout = usePersistentLayout("sqlflow.workbench.layout.h");
  const verticalLayout = usePersistentLayout("sqlflow.workbench.layout.v");

  const activeGroup = groupForPath(location.pathname);

  const persistSidebar = useCallback((open: boolean) => {
    setSidebarOpen(open);
    window.localStorage.setItem(SIDEBAR_KEY, open ? "open" : "closed");
  }, []);

  // A page may fold the side bar for the room while it needs it (useSideBarFold). The person's own choice is kept apart
  // and never overwritten: the side bar comes back as they left it once no page holds it folded, and opening it while a
  // page does keeps it open until they fold it again or the pages let go.
  const [folds, setFolds] = useState(0);
  const [openedWhileFolded, setOpenedWhileFolded] = useState(false);
  const fold = useCallback(() => {
    setFolds((held) => held + 1);
    setOpenedWhileFolded(false);
    let released = false;
    return () => {
      if (!released) {
        released = true;
        setFolds((held) => held - 1);
      }
    };
  }, []);
  const room = useMemo((): SideBarRoom => ({ fold }), [fold]);
  const shown = sidebarOpen && (folds === 0 || openedWhileFolded);

  const showSideBar = useCallback(() => {
    if (!sidebarOpen) {
      persistSidebar(true);
    }

    if (folds > 0) {
      setOpenedWhileFolded(true);
    }
  }, [sidebarOpen, folds, persistSidebar]);

  // While a page holds the side bar folded, hiding it folds it back for the page and leaves the person's choice as it was.
  const hideSideBar = useCallback(() => {
    if (folds > 0) {
      setOpenedWhileFolded(false);
    } else {
      persistSidebar(false);
    }
  }, [folds, persistSidebar]);

  const selectGroup = useCallback((group: NavGroup) => {
    if (!shown) {
      showSideBar();
      setReveal({ group: group.id, nonce: Date.now() });
      return;
    }

    // Clicking the already-revealed group collapses the side bar, VS Code style.
    const revealedId = reveal !== null && "group" in reveal ? reveal.group : activeGroup.id;
    if (group.id === revealedId) {
      hideSideBar();
      return;
    }

    setReveal({ group: group.id, nonce: Date.now() });
  }, [shown, reveal, activeGroup.id, showSideBar, hideSideBar]);

  // An activity bar of shortcuts: its menu button shows the side bar on the page shown, or hides it, and a shortcut opens
  // its page, leaving the side bar as it was: shown, with the entry brought into view; hidden, to show on the page
  // shown when the menu button next opens it.
  const toggleSideBar = useCallback(() => {
    if (shown) {
      hideSideBar();
      return;
    }

    showSideBar();
    const page = selectedNavPath(location.pathname);
    if (page !== "") {
      setReveal({ page, nonce: Date.now() });
    }
  }, [shown, location.pathname, showSideBar, hideSideBar]);

  const openShortcut = useCallback((item: NavItem) => {
    navigate(item.to);
    if (shown) {
      setReveal({ page: item.to, nonce: Date.now() });
    }
  }, [navigate, shown]);

  // A page's entry is revealed once; a group stays the revealed one, which the next click on its icon toggles.
  const revealed = useCallback(() => {
    setReveal((current) => (current !== null && "page" in current ? null : current));
  }, []);

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-background text-foreground">
      <TitleBar onOpenPalette={() => setPaletteOpen(true)} />
      <div className="flex min-h-0 flex-1">
        <ActivityBar
          activeGroupId={activeGroup.id}
          onSelectGroup={selectGroup}
          sideBarShown={shown}
          onToggleSideBar={toggleSideBar}
          onOpenShortcut={openShortcut}
        />
        <ResizablePanelGroup
          orientation="horizontal"
          defaultLayout={horizontalLayout.defaultLayout}
          onLayoutChanged={horizontalLayout.onLayoutChanged}
          className="min-w-0 flex-1"
        >
          {shown && (
            <>
              <ResizablePanel id="side-bar" defaultSize={240} minSize={180} maxSize={420} className="hidden md:block">
                <SideBar reveal={reveal} onRevealed={revealed} />
              </ResizablePanel>
              <ResizableHandle className="hidden md:flex" />
            </>
          )}
          <ResizablePanel id="editor" className="min-w-0">
            <div className="flex h-full min-h-0 flex-col">
              <TabsBar />
              <ResizablePanelGroup
                orientation="vertical"
                defaultLayout={verticalLayout.defaultLayout}
                onLayoutChanged={verticalLayout.onLayoutChanged}
              >
                {/* The page gives way to the panel all the way: a long trace can be read at the editor's whole height, and
                    the handle, which stays under the tab strip, brings the page back. */}
                <ResizablePanel id="editor-content" minSize="0">
                  <div className="relative h-full">
                    <main className="h-full overflow-y-auto bg-background" data-explain-scope>
                      <div className="mx-auto max-w-[1600px] p-4 md:p-6">
                        <SideBarRoomContext.Provider value={room}>
                          <Outlet />
                        </SideBarRoomContext.Provider>
                      </div>
                    </main>
                    {/* A repository file's code and an explanation slide in over the page from its left edge, an explanation
                        over the code it explains; maximized, either covers all but the title and status bars. */}
                    <RepoFileDrawer maximizedClassName="inset-x-0 top-9 bottom-[22px]" />
                    <ExplainDrawer maximizedClassName="inset-x-0 top-9 bottom-[22px]" />
                    {/* While a panel is open, the page fades into a shade along its bottom edge, so the panel below reads
                        as raised over the page rather than as more of it. Drawn over the content, which a shadow on the
                        scrolling element would be painted under. */}
                    {panelContent !== null && (
                      <div
                        aria-hidden
                        className="pointer-events-none absolute inset-x-0 bottom-0 h-4 bg-gradient-to-t from-foreground/10 to-transparent dark:from-black/45"
                        data-testid="bottom-panel-shade"
                      />
                    )}
                  </div>
                </ResizablePanel>
                {panelContent !== null && (
                  <>
                    <ResizableHandle />
                    <ResizablePanel id="bottom-panel" defaultSize="35" minSize="15">
                      <PanelHost />
                    </ResizablePanel>
                  </>
                )}
              </ResizablePanelGroup>
            </div>
          </ResizablePanel>
        </ResizablePanelGroup>
      </div>
      <StatusBar />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <RunDock />
    </div>
  );
}

/** The application frame: the VS Code-style workbench with the routed page as the editor content. */
export default function AppShell() {
  return (
    <TabsProvider>
      <PanelProvider>
        <ExplainProvider>
          <RepoFileWindowProvider>
            <RunDockProvider>
              {/* Innermost, so a held trace's view has every provider the bottom panel's content has. */}
              <TraceTrayProvider>
                <WorkbenchFrame />
              </TraceTrayProvider>
            </RunDockProvider>
          </RepoFileWindowProvider>
        </ExplainProvider>
      </PanelProvider>
    </TabsProvider>
  );
}
