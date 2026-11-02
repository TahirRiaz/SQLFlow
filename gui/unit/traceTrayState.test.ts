// The trace tray holds an operation's trace while it runs, asks for a look at one that ends unseen, and lets go of one
// that has been read and closed; the title bar's indicator draws what digest sums up.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_HELD_TRACES, digest, holdAsked, holdStarted, isRunning, markSeen, report, retireSeen, toneOf,
  type HeldTrace, type TraceStatus,
} from "../src/layout/workbench/traceTrayState.ts";

function trace(id: string, overrides: Partial<HeldTrace> = {}): HeldTrace {
  return { id, title: `Sync · ${id}`, status: null, unseen: false, ...overrides };
}

function status(overrides: Partial<TraceStatus> = {}): TraceStatus {
  return { ended: false, failed: false, problems: 0, errors: 0, ...overrides };
}

const ended = status({ ended: true });

describe("holdStarted", () => {
  it("adds a trace as the newest, running", () => {
    const held = holdStarted([trace("a")], trace("b"));
    assert.deepEqual(held.map((t) => t.id), ["a", "b"]);
    assert.equal(isRunning(held[1]), true);
  });

  it("starts a held trace over as the newest, its outcome and unseen mark cleared", () => {
    const held = holdStarted([trace("a", { status: ended, unseen: true }), trace("b")], trace("a"));
    assert.deepEqual(held.map((t) => t.id), ["b", "a"]);
    assert.equal(held[1].status, null);
    assert.equal(held[1].unseen, false);
  });

  it("lets the oldest go past the bound", () => {
    let held: HeldTrace[] = [];
    for (let i = 0; i <= MAX_HELD_TRACES; i += 1) {
      held = holdStarted(held, trace(`t${i}`));
    }

    assert.equal(held.length, MAX_HELD_TRACES);
    assert.equal(held[0].id, "t1");
    assert.equal(held[held.length - 1].id, `t${MAX_HELD_TRACES}`);
  });
});

describe("holdAsked", () => {
  it("keeps a held trace as it is, so asking to see it never restarts it", () => {
    const before = [trace("a", { status: ended }), trace("b")];
    const held = holdAsked(before, trace("a", { title: "renamed" }));
    assert.deepEqual(held, before);
  });

  it("adds a trace it does not hold", () => {
    assert.deepEqual(holdAsked([trace("a")], trace("b")).map((t) => t.id), ["a", "b"]);
  });
});

describe("report", () => {
  it("marks a trace that ends out of view unseen", () => {
    const held = report([trace("a", { status: status() })], "a", ended, null);
    assert.equal(held[0].unseen, true);
    assert.equal(isRunning(held[0]), false);
  });

  it("counts a first report that has already ended as an end", () => {
    assert.equal(report([trace("a")], "a", ended, null)[0].unseen, true);
  });

  it("leaves a trace that ends in view seen", () => {
    assert.equal(report([trace("a", { status: status() })], "a", ended, "a")[0].unseen, false);
  });

  it("does not mark a trace again when it reports after ending", () => {
    const held = report([trace("a", { status: ended, unseen: false })], "a", status({ ended: true, problems: 2 }), null);
    assert.equal(held[0].unseen, false);
    assert.equal(held[0].status?.problems, 2);
  });

  it("keeps a running trace's report without asking for a look", () => {
    const held = report([trace("a")], "a", status({ problems: 3 }), null);
    assert.equal(held[0].unseen, false);
    assert.equal(isRunning(held[0]), true);
  });

  it("ignores a trace the tray no longer holds", () => {
    const before = [trace("a")];
    assert.deepEqual(report(before, "gone", ended, null), before);
  });
});

describe("markSeen and retireSeen", () => {
  it("clears the unseen mark of the trace shown", () => {
    const held = markSeen([trace("a", { status: ended, unseen: true }), trace("b", { status: ended, unseen: true })], "a");
    assert.deepEqual(held.map((t) => t.unseen), [false, true]);
  });

  it("lets go of ended, read traces that are not shown, and keeps the rest", () => {
    const held = retireSeen([
      trace("read", { status: ended }),
      trace("unread", { status: ended, unseen: true }),
      trace("running", { status: status() }),
      trace("waiting"),
      trace("shown", { status: ended }),
    ], "shown");
    assert.deepEqual(held.map((t) => t.id), ["unread", "running", "waiting", "shown"]);
  });
});

describe("toneOf", () => {
  it("has no tone while running", () => {
    assert.equal(toneOf(trace("a")), null);
    assert.equal(toneOf(trace("a", { status: status({ problems: 4, errors: 1 }) })), null);
  });

  it("reads a failure or an error line as an error, a warning as a warning, and a clean end as success", () => {
    assert.equal(toneOf(trace("a", { status: status({ ended: true, failed: true }) })), "error");
    assert.equal(toneOf(trace("a", { status: status({ ended: true, problems: 3, errors: 1 }) })), "error");
    assert.equal(toneOf(trace("a", { status: status({ ended: true, problems: 18 }) })), "warning");
    assert.equal(toneOf(trace("a", { status: ended })), "success");
  });
});

describe("digest", () => {
  it("counts the running traces and has nothing to ask for when none ended unseen", () => {
    assert.deepEqual(digest([trace("a"), trace("b", { status: status() }), trace("c", { status: ended })]), {
      running: 2,
      attention: null,
    });
  });

  it("asks for a look in the worst tone of the unseen outcomes", () => {
    const success = trace("s", { status: ended, unseen: true });
    const warning = trace("w", { status: status({ ended: true, problems: 1 }), unseen: true });
    const error = trace("e", { status: status({ ended: true, failed: true }), unseen: true });
    assert.equal(digest([success]).attention, "success");
    assert.equal(digest([success, warning]).attention, "warning");
    assert.equal(digest([warning, success]).attention, "warning");
    assert.equal(digest([error, warning, success]).attention, "error");
    assert.equal(digest([success, warning, error]).attention, "error");
  });

  it("leaves a read outcome out of the attention", () => {
    const read = trace("r", { status: status({ ended: true, failed: true }) });
    assert.equal(digest([read, trace("s", { status: ended, unseen: true })]).attention, "success");
  });
});
