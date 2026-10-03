import { useCallback, useMemo, useState } from "react";
import { Outlet, useLocation } from "react-router-dom";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { RunDock } from "../features/runs/RunDock";
import { RunDockProvider } from "../features/runs/RunDockContext";
import { groupForPath, type NavGroup } from "./nav";
import { ActivityBar } from "./workbench/ActivityBar";
import { CommandPalette } from "./workbench/CommandPalette";
import { PanelHost } from "./workbench/PanelHost";
import { PanelProvider, usePanel } from "./workbench/PanelContext";
import { SideBar } from "./workbench/SideBar";
import { SideBarRoomContext, type SideBarRoom } from "./workbench/SideBarRoom";
import { StatusBar } from "./workbench/StatusBar";
import { TabsBar } from "./workbench/TabsBar";
import { TabsProvider } from "./workbench/TabsContext";
import { TitleBar } from "./workbench/TitleBar";
import { usePersistentLayout } from "./workbench/usePersistentLayout";

const SIDEBAR_KEY = "sqlflow.workbench.sidebar";

/**
 * The workbench frame (DESIGN.md section 6): title bar over activity bar + resizable side bar +
 * (tab strip, editor, bottom panel) over the status bar. Only the editor and panel scroll.
 */
function WorkbenchFrame() {
  const location = useLocation();
  const [sidebarOpen, setSidebarOpen] = useState(() => window.localStorage.getItem(SIDEBAR_KEY) !== "closed");
  const [reveal, setReveal] = useState<{ id: string; nonce: number } | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const { content: panelContent } = usePanel();
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

  const selectGroup = useCallback((group: NavGroup) => {
    if (!shown) {
      if (!sidebarOpen) {
        persistSidebar(true);
      }

      if (folds > 0) {
        setOpenedWhileFolded(true);
      }

      setReveal({ id: group.id, nonce: Date.now() });
      return;
    }

    // Clicking the already-revealed group collapses the side bar, VS Code style; while a page holds it folded, that folds
    // it back for the page and leaves the person's choice as it was.
    const revealedId = reveal?.id ?? activeGroup.id;
    if (group.id === revealedId) {
      if (folds > 0) {
        setOpenedWhileFolded(false);
      } else {
        persistSidebar(false);
      }

      return;
    }

    setReveal({ id: group.id, nonce: Date.now() });
  }, [shown, sidebarOpen, folds, reveal, activeGroup.id, persistSidebar]);

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-background text-foreground">
      <TitleBar onOpenPalette={() => setPaletteOpen(true)} />
      <div className="flex min-h-0 flex-1">
        <ActivityBar activeGroupId={activeGroup.id} onSelectGroup={selectGroup} />
        <ResizablePanelGroup
          orientation="horizontal"
          defaultLayout={horizontalLayout.defaultLayout}
          onLayoutChanged={horizontalLayout.onLayoutChanged}
          className="min-w-0 flex-1"
        >
          {shown && (
            <>
              <ResizablePanel id="side-bar" defaultSize={240} minSize={180} maxSize={420} className="hidden md:block">
                <SideBar reveal={reveal} />
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
                <ResizablePanel id="editor-content" minSize="20">
                  <main className="h-full overflow-y-auto bg-background">
                    <div className="mx-auto max-w-[1600px] p-4 md:p-6">
                      <SideBarRoomContext.Provider value={room}>
                        <Outlet />
                      </SideBarRoomContext.Provider>
                    </div>
                  </main>
                </ResizablePanel>
                {panelContent !== null && (
                  <>
                    <ResizableHandle />
                    <ResizablePanel id="bottom-panel" defaultSize="35" minSize="15" maxSize="70">
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
        <RunDockProvider>
          <WorkbenchFrame />
        </RunDockProvider>
      </PanelProvider>
    </TabsProvider>
  );
}
