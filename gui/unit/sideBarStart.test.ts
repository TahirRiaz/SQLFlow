// How the workbench's side bar starts: the person's own choice first, and only before they made one, what a module asks.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { sideBarStartsOpen } from "../src/layout/workbench/sideBarStart.ts";

describe("sideBarStartsOpen", () => {
  it("starts open on a first visit when no module asks otherwise, as SQLFlow ships", () => {
    assert.equal(sideBarStartsOpen(null, false), true);
  });

  it("starts closed on a first visit when a module asks for it", () => {
    assert.equal(sideBarStartsOpen(null, true), false);
  });

  it("keeps the person's choice, whatever a module asks", () => {
    for (const closedOnFirstVisit of [false, true]) {
      assert.equal(sideBarStartsOpen("open", closedOnFirstVisit), true, String(closedOnFirstVisit));
      assert.equal(sideBarStartsOpen("closed", closedOnFirstVisit), false, String(closedOnFirstVisit));
    }
  });

  it("reads a stored value it does not know as open, as the frame always has", () => {
    for (const stored of ["", " ", "Closed", "CLOSED", "false", "0", "{}"]) {
      assert.equal(sideBarStartsOpen(stored, true), true, JSON.stringify(stored));
      assert.equal(sideBarStartsOpen(stored, false), true, JSON.stringify(stored));
    }
  });
});
