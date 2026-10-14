// How the workbench's side bar starts. Kept apart from the frame, with nothing to import, so the unit suite reads it.

/**
 * Whether the side bar starts open: as the person last left it in this browser, or, before they have ever opened or
 * closed it (`stored` is null), open unless a registered module asks for it closed (`GuiModule.sideBar`). `stored` is
 * what the frame keeps the person's choice as, "open" or "closed"; anything else reads as open, as it always has.
 */
export function sideBarStartsOpen(stored: string | null, closedOnFirstVisit: boolean): boolean {
  return stored === null ? !closedOnFirstVisit : stored !== "closed";
}
