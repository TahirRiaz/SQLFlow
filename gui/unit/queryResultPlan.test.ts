// How a query result is drawn: what kind each column is, which form a shape fits, and the values a chart plots.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  axisLabels, barData, classify, columnKey, columnLabel, linePoints, planChart, scaleSpread, toNumber,
} from "../src/features/chat/queryResultPlan.ts";

describe("classify", () => {
  it("reads a provider's type name whatever its precision, case or spacing", () => {
    assert.equal(classify("decimal(10,2)"), "numeric");
    assert.equal(classify("NUMBER(10,2)"), "numeric");
    assert.equal(classify("bit"), "numeric");
    assert.equal(classify("double  precision"), "numeric");
    assert.equal(classify("datetime2(7)"), "date");
    assert.equal(classify("timestamp(6) with time zone"), "date");
    assert.equal(classify("time"), "date");
    assert.equal(classify("nvarchar(max)"), "text");
    assert.equal(classify("int unsigned"), "text");
    assert.equal(classify(""), "text");
  });
});

describe("toNumber", () => {
  it("is a finite number or nothing", () => {
    assert.equal(toNumber("148300.00"), 148300);
    assert.equal(toNumber("-1"), -1);
    assert.equal(toNumber("1e3"), 1000);
    for (const value of [null, "", " ", "NaN", "Infinity", "abc"]) {
      assert.equal(toNumber(value), null, String(value));
    }
  });
});

describe("planChart", () => {
  it("draws nothing for no rows", () => {
    assert.deepEqual(planChart(["numeric"], 0), { form: "empty" });
  });

  it("shows a single row of one to four numbers as stat tiles", () => {
    assert.deepEqual(planChart(["numeric", "numeric"], 1), { form: "stat", numericIdx: [0, 1] });
    assert.deepEqual(planChart(["numeric", "numeric", "numeric", "numeric", "numeric"], 1), { form: "table" });
  });

  it("shows a single row that also carries text or a date as a table, rather than dropping them", () => {
    assert.deepEqual(planChart(["text", "numeric"], 1), { form: "table" });
    assert.deepEqual(planChart(["date", "numeric"], 1), { form: "table" });
  });

  it("draws one date and one to four numbers over several rows as a line", () => {
    assert.deepEqual(planChart(["date", "numeric", "numeric"], 5), { form: "line", dateIdx: 0, numericIdx: [1, 2] });
    assert.deepEqual(planChart(["date", "date", "numeric"], 5), { form: "table" });
    assert.deepEqual(planChart(["date", "text", "numeric"], 5), { form: "table" });
  });

  it("draws one category and one number over several rows as bars", () => {
    assert.deepEqual(planChart(["text", "numeric"], 3), { form: "bar", textIdx: 0, numericIdx: 1 });
    assert.deepEqual(planChart(["text", "numeric", "numeric"], 3), { form: "table" });
    assert.deepEqual(planChart(["text", "numeric", "date"], 3), { form: "table" });
  });
});

describe("columns", () => {
  it("are keyed by position, so two alike or unnamed columns never collide", () => {
    const columns = [{ name: "", dataType: "int" }, { name: "", dataType: "int" }, { name: "Total", dataType: "int" }];
    assert.notEqual(columnKey(0), columnKey(1));
    assert.equal(columnLabel(columns, 0), "(column 1)");
    assert.equal(columnLabel(columns, 1), "(column 2)");
    assert.equal(columnLabel(columns, 2), "Total");
    assert.equal(columnLabel(columns, 9), "(column 10)");
  });

  it("plot each series under its own key", () => {
    const points = linePoints(
      [["2025-01-02", "1", "2"], ["2025-01-01", "3", "4"]], 0, "date", [1, 2]);
    assert.deepEqual(points, [
      { date: "2025-01-01", [columnKey(1)]: 3, [columnKey(2)]: 4 },
      { date: "2025-01-02", [columnKey(1)]: 1, [columnKey(2)]: 2 },
    ]);
  });
});

describe("barData", () => {
  it("draws no bar, not a zero, for a row without a number", () => {
    assert.deepEqual(barData([["North", "10"], ["South", null], [null, "x"]], 0, 1, 10), [
      { category: "North", value: 10 },
      { category: "South", value: null },
      { category: "(null)", value: null },
    ]);
  });

  it("keeps the first rows up to the cap", () => {
    assert.equal(barData([["a", "1"], ["b", "2"], ["c", "3"]], 0, 1, 2).length, 2);
    assert.equal(barData([["a", "1"]], 0, 1, -1).length, 0);
  });
});

describe("axisLabels", () => {
  it("shows a date alone when every value falls on midnight", () => {
    assert.deepEqual(
      axisLabels(["2025-01-01T00:00:00.0000000", "2025-01-02 00:00:00", "2025-01-03"], "datetime2"),
      ["2025-01-01", "2025-01-02", "2025-01-03"]);
  });

  it("keeps a time of day when the series has one, to the minute or to the second", () => {
    assert.deepEqual(
      axisLabels(["2025-01-01T09:00:00", "2025-01-01T10:00:00"], "datetime"),
      ["2025-01-01 09:00", "2025-01-01 10:00"]);
    assert.deepEqual(
      axisLabels(["2025-01-01T09:00:00", "2025-01-01T09:00:30.5"], "datetime2"),
      ["2025-01-01 09:00:00", "2025-01-01 09:00:30"]);
  });

  it("shows a time column as its time, and an unrecognised or missing value as it is", () => {
    assert.deepEqual(axisLabels(["13:45:00.0000000", "14:00:00"], "time"), ["13:45", "14:00"]);
    assert.deepEqual(axisLabels(["13:45:10", null], "time(7)"), ["13:45:10", ""]);
    assert.deepEqual(axisLabels(["week 1", null], "date"), ["week 1", ""]);
  });
});

describe("scaleSpread", () => {
  it("is the largest series over the smallest, ignoring series with no magnitude", () => {
    const points = [{ a: 1000, b: 10, c: 0 }, { a: -2000, b: null, c: 0 }];
    assert.equal(scaleSpread(points, ["a", "b"]), 200);
    assert.equal(scaleSpread(points, ["a", "c"]), 1);
    assert.equal(scaleSpread([], ["a"]), 1);
  });
});
