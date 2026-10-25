// How the Maintenance page words an area: the grid's cleanup cell for an area with a limit, one without, one that cleans
// itself and one that is kept, the tables it holds, and what the search finds.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { StorageArea } from "../src/api/types.ts";
import {
  cleanableAreas, cleanupSummary, daysFit, daysText, estimateFreed, holdingText, initialSelection, matchesSearch, verdictOrder,
} from "../src/features/maintenance/storageText.ts";

function area(overrides: Partial<StorageArea>): StorageArea {
  return {
    id: "sqlflow.run-statements",
    owner: "SQLFlow",
    group: "Run history",
    title: "SQL statements",
    verdict: "safe",
    holds: "The SQL each run generated.",
    reason: "A superseded copy.",
    lifecycle: null,
    brief: null,
    grows: false,
    managedAt: "/runs",
    claims: ["catalog.RunStatement"],
    column: null,
    tables: [{ schema: "catalog", name: "RunStatement", rows: 10, bytes: 8192 }],
    rows: 10,
    bytes: 8192,
    measureError: null,
    retention: null,
    lastCleanup: null,
    running: false,
    ...overrides,
  };
}

const retention = {
  keeps: "Each flow's latest run.", unit: "statements", minDays: 0, maxDays: 36500, days: null as number | null,
  updatedUtc: null, updatedBy: null, reclaimable: null as number | null, reclaimableError: null as string | null,
};

describe("cleanupSummary", () => {
  it("says an area with no limit stays until one is set", () => {
    assert.deepEqual(cleanupSummary(area({ retention })), { headline: "No limit set", detail: "Stays until an admin sets one", grows: false });
  });

  it("says the limit and how many of the area's unit it would remove now", () => {
    const summary = cleanupSummary(area({ retention: { ...retention, days: 30, reclaimable: 1204 } }));
    assert.equal(summary.headline, "Older than 30 days");
    assert.equal(summary.detail, `${(1204).toLocaleString()} statements to remove`);
  });

  it("says when nothing is old enough, and when the count failed", () => {
    assert.equal(cleanupSummary(area({ retention: { ...retention, days: 1, reclaimable: 0 } })).detail, "Nothing to remove now");
    assert.equal(cleanupSummary(area({ retention: { ...retention, days: 1, reclaimableError: "timeout" } })).detail, "Could not count what would go");
  });

  it("gives an area that cleans itself its rule", () => {
    assert.deepEqual(cleanupSummary(area({ brief: "After 30 days", lifecycle: "Removed automatically 30 days after it was raised." })), {
      headline: "After 30 days", detail: "Cleans itself", grows: false,
    });
  });

  it("flags a kept area that grows with nothing removing it", () => {
    assert.deepEqual(cleanupSummary(area({ verdict: "keep", brief: "Never removed", grows: true })), {
      headline: "Never removed", detail: "Grows with use", grows: true,
    });
    assert.deepEqual(cleanupSummary(area({ verdict: "keep", brief: "Rewritten by each build" })), {
      headline: "Rewritten by each build", detail: null, grows: false,
    });
  });
});

describe("holdingText", () => {
  it("names one table, counts several, and names a cleared column", () => {
    assert.equal(holdingText(area({})), "catalog.RunStatement");
    assert.equal(holdingText(area({ tables: [area({}).tables[0], { schema: "catalog", name: "Run", rows: 1, bytes: 1 }] })), "2 tables");
    assert.equal(holdingText(area({ tables: [], column: { schema: "mod", table: "Activity", column: "Log" } })), "Log of mod.Activity");
    assert.equal(holdingText(area({ tables: [], claims: ["mod.tmp_*"] })), "mod.tmp_*");
  });
});

describe("matchesSearch", () => {
  it("matches every word against the title, what it holds, its subject, owner and tables", () => {
    assert.ok(matchesSearch(area({}), ""));
    assert.ok(matchesSearch(area({}), "sql runstatement"));
    assert.ok(matchesSearch(area({}), "run history"));
    assert.ok(!matchesSearch(area({}), "sql attempts"));
    assert.ok(matchesSearch(area({ tables: [], column: { schema: "mod", table: "Activity", column: "Log" } }), "activity.log"));
  });
});

describe("words", () => {
  it("counts days, and reads the verdicts from no loss to kept", () => {
    assert.equal(daysText(1), "1 day");
    assert.equal(daysText(365), "365 days");
    assert.deepEqual(verdictOrder, ["safe", "optional", "keep"]);
  });
});

describe("the cleanup panel", () => {
  const statements = area({ retention });
  const attempts = area({
    id: "mod.attempts", verdict: "optional", title: "Attempts", bytes: 10_000, rows: 100,
    tables: [{ schema: "mod", name: "Attempt", rows: 100, bytes: 10_000 }], retention: { ...retention, unit: "attempts", minDays: 1 },
  });
  const runs = area({ id: "mod.runs", verdict: "optional", tables: [], claims: ["mod.Run", "mod.Result"], retention: { ...retention, unit: "runs" } });
  const kept = area({ id: "mod.records", verdict: "keep", retention: null });

  it("lists only what a cleanup can take, safe first, the largest first", () => {
    const big = area({ id: "mod.big", verdict: "optional", bytes: 99_999, retention: { ...retention, unit: "rows" } });
    assert.deepEqual(cleanableAreas([kept, attempts, statements, big]).map((a) => a.id), [statements.id, big.id, attempts.id]);
  });

  it("starts with what is safe ticked, or only the area it was opened for", () => {
    const cleanable = cleanableAreas([statements, attempts, runs]);
    assert.deepEqual([...initialSelection(cleanable, null)], [statements.id]);
    assert.deepEqual([...initialSelection(cleanable, attempts.id)], [attempts.id]);
    assert.deepEqual([...initialSelection(cleanable, "mod.unknown")], []);
  });

  it("estimates what a cleanup frees from the share of the area's rows, and not where the unit is not a row", () => {
    assert.equal(estimateFreed(attempts, 25), 2_500);
    assert.equal(estimateFreed(attempts, 0), 0);
    assert.equal(estimateFreed(attempts, 500), 10_000);
    assert.equal(estimateFreed(runs, 3), null);
  });

  it("takes only the days an area's limit takes", () => {
    assert.ok(daysFit(statements, 0));
    assert.ok(!daysFit(attempts, 0));
    assert.ok(daysFit(attempts, 30));
    assert.ok(!daysFit(kept, 30));
  });
});
