// A sweep over a paged list: every page until the server has none left, stopping at the cap and saying it did, so a
// dropdown of every schedule or a tree of every pipeline is never silently cut at the server's page size.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { PagedResult } from "../src/api/types.ts";
import { fetchEveryPage, SWEEP_PAGE_SIZE } from "../src/lib/fetchEveryPage.ts";

/** A server holding `count` rows (numbered from 0), answering pages the way the API does; `asked` records each call. */
function server(count: number, asked: [number, number][] = []) {
  return async (page: number, pageSize: number): Promise<PagedResult<number>> => {
    asked.push([page, pageSize]);
    const start = (page - 1) * pageSize;
    const items = Array.from({ length: Math.max(0, Math.min(pageSize, count - start)) }, (_, i) => start + i);
    return { items, page, pageSize, total: count };
  };
}

describe("fetchEveryPage", () => {
  it("reads past the server's page size until every row is in", async () => {
    const asked: [number, number][] = [];
    const sweep = await fetchEveryPage(server(450, asked), 5000);

    assert.equal(sweep.items.length, 450);
    assert.equal(sweep.items[449], 449);
    assert.equal(sweep.total, 450);
    assert.equal(sweep.capped, false);
    assert.deepEqual(asked, [[1, SWEEP_PAGE_SIZE], [2, SWEEP_PAGE_SIZE], [3, SWEEP_PAGE_SIZE]]);
  });

  it("stops at the cap and says it stopped short", async () => {
    const sweep = await fetchEveryPage(server(1000), 400);

    assert.equal(sweep.items.length, 400);
    assert.equal(sweep.total, 1000);
    assert.equal(sweep.capped, true);
  });

  it("asks once when nothing matches", async () => {
    const asked: [number, number][] = [];
    const sweep = await fetchEveryPage(server(0, asked), 5000);

    assert.deepEqual(sweep, { items: [], total: 0, capped: false });
    assert.equal(asked.length, 1);
  });

  it("ends on an empty page when the total shrinks while it reads", async () => {
    let calls = 0;
    const shrinking = async (page: number, pageSize: number): Promise<PagedResult<number>> => {
      calls += 1;
      // The first page claims 500 rows; by the second, the rows behind it are gone but the count has not caught up.
      return page === 1
        ? { items: Array.from({ length: pageSize }, (_, i) => i), page, pageSize, total: 500 }
        : { items: [], page, pageSize, total: 500 };
    };
    const sweep = await fetchEveryPage(shrinking, 5000);

    assert.equal(calls, 2);
    assert.equal(sweep.items.length, SWEEP_PAGE_SIZE);
    assert.equal(sweep.capped, true);
  });
});
