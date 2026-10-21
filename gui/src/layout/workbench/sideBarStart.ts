// How the workbench's side bar starts. Kept apart from the frame, with nothing to import, so the unit suite reads it.

/**
 * Whether the side bar starts open: as the person last left it in this browser, or, before they have ever opened or
 * closed it (`stored` is null), open unless a registered module asks for it closed (`GuiModule.sideBar`). `stored` is
 * what the frame keeps the person's choice as, "open" or "closed"; anything else reads as open, as it always has.
 */
export function sideBarStartsOpen(stored: string | null, closedOnFirstVisit: boolean): boolean {
  return stored === null ? !closedOnFirstVisit : stored !== "closed";
}

/**
 * Which of the menu's groups and sections start collapsed: those the person left collapsed in this browser, or, before
 * the browser keeps any (`stored` is null), none, or every one of `all` when a registered module asks for the menu
 * collapsed (`GuiModule.sideBar`). `stored` is the JSON list of ids the side bar keeps; one it cannot read keeps none
 * collapsed, as it always has, and an id it holds that the menu no longer has does no harm.
 */
export function menuCollapsedAtStart(
  stored: string | null,
  collapsedOnFirstVisit: boolean,
  all: readonly string[],
): ReadonlySet<string> {
  if (stored === null) {
    return new Set(collapsedOnFirstVisit ? all : []);
  }

  try {
    const parsed: unknown = JSON.parse(stored);
    return new Set(Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === "string") : []);
  } catch {
    // A corrupt store just means every group and section starts expanded.
    return new Set();
  }
}
