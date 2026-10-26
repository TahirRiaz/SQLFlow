import { Outlet } from "react-router-dom";
import { ExplainDrawer, ExplainProvider } from "../features/chat/ExplainContext";
import { RepoFileDrawer, RepoFileWindowProvider } from "../features/repos/RepoFileWindow";

/**
 * The frame of a page a module opens in a window of its own: the whole viewport in the workbench's colours, and nothing
 * else of the workbench (no title bar, activity bar, side bar, tabs or status bar) but a repository file's code and an
 * explanation, which slide in over the page as they do in the workbench. The page is a flex column child, so a page that
 * fills the window gives its root `min-h-0 flex-1`; one taller than the window scrolls here.
 */
export default function WindowFrame() {
  return (
    <ExplainProvider>
      <RepoFileWindowProvider>
        <div className="relative flex h-screen flex-col bg-background text-foreground" data-testid="window-frame">
          <div className="flex min-h-0 flex-1 flex-col overflow-y-auto" data-explain-scope>
            <Outlet />
          </div>
          <RepoFileDrawer maximizedClassName="inset-0" />
          <ExplainDrawer maximizedClassName="inset-0" />
        </div>
      </RepoFileWindowProvider>
    </ExplainProvider>
  );
}
