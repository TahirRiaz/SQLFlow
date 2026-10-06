import { useCallback } from "react";
import { parsePath, useLocation, useNavigate } from "react-router-dom";
import { usePanel } from "./PanelContext";

/**
 * Navigation from the workbench's menus: the side bar and its search, the mobile navigation sheet, the command palette
 * and the account menu. Going to another page closes the bottom panel, because what it shows (a run's trace, a sync's
 * log, a discovery's progress) belongs to the page being left; choosing the page already shown leaves it open. A page
 * that raises the panel for what it shows, as a run's page raises its trace, raises it again when it is next opened.
 */
export function useMenuNavigate(): (to: string) => void {
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const { close } = usePanel();

  return useCallback((to: string) => {
    if ((parsePath(to).pathname ?? pathname) !== pathname) {
      close();
    }

    navigate(to);
  }, [navigate, pathname, close]);
}
