// The tab strip's geometry: which of its ends hide tabs, where it scrolls to bring a tab into view, where a step of its
// scroll buttons lands, how a mouse wheel moves it, and which tab a key moves the focus to. Pure, so the rules are tested
// without a browser; TabsBar reads the measures from the DOM and applies what these answer.

/** The strip's scroll box: how far it is scrolled, how wide its view is, how wide all its tabs are together. */
export interface StripView {
  scrollLeft: number;
  clientWidth: number;
  scrollWidth: number;
}

/** A tab's place along the strip, from the start of its content (offsetLeft) and its width. */
export interface TabSpan {
  left: number;
  width: number;
}

/** Whether tabs lie hidden before the view's start and after its end. */
export interface StripEdges {
  before: boolean;
  after: boolean;
}

/** Browsers report fractional scroll offsets and round widths, so a strip within a pixel of an end is at that end. */
const SLACK = 1;

/** The pixels a line of a line-mode wheel (Firefox's) moves the strip: about one row of text. */
const LINE_PIXELS = 16;

export function stripEdges(view: StripView): StripEdges {
  const hidden = view.scrollWidth - view.clientWidth;
  if (hidden <= SLACK) {
    return { before: false, after: false };
  }

  return { before: view.scrollLeft > SLACK, after: view.scrollLeft < hidden - SLACK };
}

function clampScroll(view: StripView, left: number): number {
  return Math.min(Math.max(0, left), Math.max(0, view.scrollWidth - view.clientWidth));
}

/**
 * The scroll offset that shows the whole of a tab, moving the strip as little as it can: a tab already in view leaves it
 * where it is, one cut at the end is brought in to the end, and one cut at the start (or wider than the view) to the start.
 */
export function revealOffset(view: StripView, tab: TabSpan): number {
  const end = tab.left + tab.width;
  if (tab.left >= view.scrollLeft - SLACK && end <= view.scrollLeft + view.clientWidth + SLACK) {
    return view.scrollLeft;
  }

  if (tab.left < view.scrollLeft || tab.width >= view.clientWidth) {
    return clampScroll(view, tab.left);
  }

  return clampScroll(view, end - view.clientWidth);
}

/**
 * Where a scroll button's step lands: a view's width of tabs further on, laid on a tab's edge so no tab is left cut at
 * the side the strip moved towards. Forward, the first tab the view's end cuts (or hides) becomes the first in view;
 * backward, the last tab its start cuts becomes the last in view. A tab wider than the view is shown from its start.
 */
export function pageOffset(view: StripView, tabs: readonly TabSpan[], direction: "before" | "after"): number {
  const viewEnd = view.scrollLeft + view.clientWidth;
  if (direction === "after") {
    // A tab that starts at the view's start already leads it (one wider than the view does): the step goes past it.
    const next = tabs.find((tab) => tab.left > view.scrollLeft + SLACK && tab.left + tab.width > viewEnd + SLACK);
    return next === undefined ? clampScroll(view, view.scrollWidth) : clampScroll(view, next.left);
  }

  let previous: TabSpan | undefined;
  for (const tab of tabs) {
    if (tab.left < view.scrollLeft - SLACK) {
      previous = tab;
    }
  }

  if (previous === undefined) {
    return 0;
  }

  const previousEnd = previous.left + previous.width;
  return previous.width >= view.clientWidth
    ? clampScroll(view, previous.left)
    : clampScroll(view, previousEnd - view.clientWidth);
}

/**
 * How far a wheel turn moves the strip, or null when the browser moves it by itself. A vertical wheel scrolls the strip
 * along (a mouse has no other way to reach a hidden tab); a sideways one (a touchpad's swipe, Shift with a wheel) is the
 * browser's own horizontal scroll, which it already applies.
 */
export function wheelScroll(deltaX: number, deltaY: number, deltaMode: number, shiftKey: boolean, pageWidth: number): number | null {
  if (shiftKey || deltaY === 0 || Math.abs(deltaX) >= Math.abs(deltaY)) {
    return null;
  }

  switch (deltaMode) {
    case 1:
      return deltaY * LINE_PIXELS;
    case 2:
      return deltaY * pageWidth;
    default:
      return deltaY;
  }
}

/**
 * The tab a key moves the focus to along the strip, as a tab list is walked (WAI-ARIA tabs): the arrows step to the
 * neighbour, wrapping at either end, and Home and End go to the first and last. Null for any other key.
 */
export function focusTarget(key: string, index: number, count: number): number | null {
  if (count === 0) {
    return null;
  }

  switch (key) {
    case "ArrowRight":
      return (index + 1) % count;
    case "ArrowLeft":
      return (index - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return null;
  }
}
