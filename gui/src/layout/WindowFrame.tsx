import { Outlet } from "react-router-dom";

/**
 * The frame of a page a module opens in a window of its own: the whole viewport in the workbench's colours, and nothing
 * else of the workbench (no title bar, activity bar, side bar, tabs or status bar). The page is a flex column child, so
 * a page that fills the window gives its root `min-h-0 flex-1`; one taller than the window scrolls here.
 */
export default function WindowFrame() {
  return (
    <div className="flex h-screen flex-col overflow-y-auto bg-background text-foreground" data-testid="window-frame">
      <Outlet />
    </div>
  );
}
