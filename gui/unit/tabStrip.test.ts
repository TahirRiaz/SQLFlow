// The workbench tab strip keeps every tab within reach: it says which ends hide tabs, brings a tab into view by the
// least scroll, steps a view's width along on a tab's edge, turns a vertical wheel into a sideways scroll, and walks its
// tabs from the keyboard.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { focusTarget, pageOffset, revealOffset, stripEdges, wheelScroll, type StripView, type TabSpan } from "../src/layout/workbench/tabStrip.ts";

/** Ten tabs of 100px side by side, the strip's view 350px wide. */
const TABS: TabSpan[] = Array.from({ length: 10 }, (_, index) => ({ left: index * 100, width: 100 }));

function view(scrollLeft: number, clientWidth = 350, scrollWidth = 1000): StripView {
  return { scrollLeft, clientWidth, scrollWidth };
}

describe("stripEdges", () => {
  it("hides nothing when the tabs fit", () => {
    assert.deepEqual(stripEdges(view(0, 400, 400)), { before: false, after: false });
  });

  it("treats a strip within a pixel of fitting as fitting", () => {
    assert.deepEqual(stripEdges(view(0, 400, 400.6)), { before: false, after: false });
  });

  it("hides tabs after the view at the start, before it at the end, and on both sides between", () => {
    assert.deepEqual(stripEdges(view(0)), { before: false, after: true });
    assert.deepEqual(stripEdges(view(650)), { before: true, after: false });
    assert.deepEqual(stripEdges(view(649.5)), { before: true, after: false });
    assert.deepEqual(stripEdges(view(300)), { before: true, after: true });
  });
});

describe("revealOffset", () => {
  it("leaves the strip where it is for a tab already in view", () => {
    assert.equal(revealOffset(view(100), TABS[2]), 100);
  });

  it("brings a tab past the end in to the end", () => {
    assert.equal(revealOffset(view(0), TABS[5]), 250);
  });

  it("brings the last tab, opened past the end, fully into view", () => {
    assert.equal(revealOffset(view(0), TABS[9]), 650);
  });

  it("brings a tab before the start in to the start", () => {
    assert.equal(revealOffset(view(500), TABS[1]), 100);
  });

  it("shows a tab wider than the view from its start", () => {
    assert.equal(revealOffset(view(0, 80), TABS[3]), 300);
  });

  it("never scrolls past either end", () => {
    assert.equal(revealOffset(view(0, 350, 1000), { left: 990, width: 100 }), 650);
    assert.equal(revealOffset(view(200), { left: -10, width: 50 }), 0);
  });
});

describe("pageOffset", () => {
  it("steps forward to the tab the view's end cuts", () => {
    // In view: tabs 0 to 2 whole, tab 3 cut at 350; the step makes tab 3 the first in view.
    assert.equal(pageOffset(view(0), TABS, "after"), 300);
  });

  it("steps forward to the first hidden tab when the view ends on a tab's edge", () => {
    assert.equal(pageOffset(view(0, 300), TABS, "after"), 300);
  });

  it("stops forward at the end", () => {
    assert.equal(pageOffset(view(500), TABS, "after"), 650);
  });

  it("steps back so the tab the view's start cuts is the last in view", () => {
    // In view from 450: tab 4 is cut at the start; the step ends the view at tab 4's end.
    assert.equal(pageOffset(view(450), TABS, "before"), 150);
  });

  it("steps back to the start from near it", () => {
    assert.equal(pageOffset(view(120), TABS, "before"), 0);
  });

  it("stays at the start when nothing lies before it", () => {
    assert.equal(pageOffset(view(0), TABS, "before"), 0);
  });

  it("shows a tab wider than the view from its start in either direction", () => {
    const wide: TabSpan[] = [{ left: 0, width: 500 }, { left: 500, width: 500 }];
    assert.equal(pageOffset(view(0, 350), wide, "after"), 500);
    assert.equal(pageOffset(view(650, 350), wide, "before"), 500);
  });
});

describe("wheelScroll", () => {
  it("turns a vertical wheel into a sideways scroll", () => {
    assert.equal(wheelScroll(0, 120, 0, false, 350), 120);
    assert.equal(wheelScroll(0, -120, 0, false, 350), -120);
  });

  it("leaves a sideways swipe and Shift with a wheel to the browser", () => {
    assert.equal(wheelScroll(80, 10, 0, false, 350), null);
    assert.equal(wheelScroll(0, 120, 0, true, 350), null);
    assert.equal(wheelScroll(0, 0, 0, false, 350), null);
  });

  it("measures a line-mode wheel in rows of text and a page-mode one in views", () => {
    assert.equal(wheelScroll(0, 3, 1, false, 350), 48);
    assert.equal(wheelScroll(0, 1, 2, false, 350), 350);
  });
});

describe("focusTarget", () => {
  it("steps to the neighbour and wraps at either end", () => {
    assert.equal(focusTarget("ArrowRight", 0, 4), 1);
    assert.equal(focusTarget("ArrowRight", 3, 4), 0);
    assert.equal(focusTarget("ArrowLeft", 0, 4), 3);
    assert.equal(focusTarget("ArrowLeft", 2, 4), 1);
  });

  it("goes to the first and the last tab", () => {
    assert.equal(focusTarget("Home", 2, 4), 0);
    assert.equal(focusTarget("End", 1, 4), 3);
  });

  it("ignores other keys and an empty strip", () => {
    assert.equal(focusTarget("Tab", 1, 4), null);
    assert.equal(focusTarget("ArrowRight", 0, 0), null);
  });
});
