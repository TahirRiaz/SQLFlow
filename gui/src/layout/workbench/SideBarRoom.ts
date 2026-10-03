import { createContext, useContext, useEffect } from "react";

/** What a page may ask of the workbench's side bar: the room it takes, for as long as the page needs it. */
export interface SideBarRoom {
  /** Folds the side bar until the function returned is called, which gives the room back. */
  fold: () => () => void;
}

/** The workbench's side bar, as its frame lends its room; null outside the workbench (a page in a window of its own). */
export const SideBarRoomContext = createContext<SideBarRoom | null>(null);

/**
 * Folds the workbench's side bar while `active` is true, for a page that needs the width (a tool docked beside what it
 * works on). The side bar comes back as the person left it once `active` turns false or the page goes; a person who opens
 * it meanwhile keeps it open, and their choice of open or closed is never overwritten. Outside the workbench it does
 * nothing.
 */
export function useSideBarFold(active: boolean): void {
  const room = useContext(SideBarRoomContext);
  useEffect(() => {
    if (!active || room === null) {
      return undefined;
    }

    return room.fold();
  }, [active, room]);
}
