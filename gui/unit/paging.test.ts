// Where a page sits in a list, and which rows of a list held in memory a page shows: the arithmetic every paged table's
// foot reads, whether a server pages its rows or the page holds them.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { pageOfRows, pageSpan } from "../src/lib/paging.ts";

const numbers = (count: number) => Array.from({ length: count }, (_, i) => i + 1);

describe("pageSpan", () => {
  it("counts the rows a page shows from 1, the last page from 0", () => {
    assert.deepEqual(pageSpan(1038, 0, 50), { from: 1, to: 50, lastPage: 20 });
    assert.deepEqual(pageSpan(1038, 20, 50), { from: 1001, to: 1038, lastPage: 20 });
  });

  it("ends a list that fills its last page exactly on that page", () => {
    assert.deepEqual(pageSpan(100, 1, 50), { from: 51, to: 100, lastPage: 1 });
  });

  it("says an empty list shows nothing and has one page", () => {
    assert.deepEqual(pageSpan(0, 0, 50), { from: 0, to: 0, lastPage: 0 });
  });

  it("keeps a list shorter than a page on its first", () => {
    assert.deepEqual(pageSpan(16, 0, 25), { from: 1, to: 16, lastPage: 0 });
  });
});

describe("pageOfRows", () => {
  it("shows the rows of the page asked for", () => {
    assert.deepEqual(pageOfRows(numbers(60), 1, 25), { rows: numbers(50).slice(25), page: 1 });
    assert.deepEqual(pageOfRows(numbers(60), 2, 25), { rows: [51, 52, 53, 54, 55, 56, 57, 58, 59, 60], page: 2 });
  });

  it("shows the last page when the list shrank under the one asked for", () => {
    assert.deepEqual(pageOfRows(numbers(30), 3, 25), { rows: [26, 27, 28, 29, 30], page: 1 });
  });

  it("shows the first page of a list that is now shorter than a page", () => {
    assert.deepEqual(pageOfRows(numbers(3), 4, 50), { rows: [1, 2, 3], page: 0 });
  });

  it("shows an empty page of an empty list", () => {
    assert.deepEqual(pageOfRows([], 2, 50), { rows: [], page: 0 });
  });

  it("never shows a page before the first", () => {
    assert.deepEqual(pageOfRows(numbers(5), -1, 50), { rows: numbers(5), page: 0 });
  });
});
