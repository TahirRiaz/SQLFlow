// A pipeline page's tabs: a flow kind's own lead and the built-in ones follow, unless the kind names an order, which puts
// the tabs it names first in its order and keeps the rest as they stand.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { orderPipelineTabs } from "../src/features/pipelines/pipelineTabs.ts";

/** A kind's own tabs, then the built-in ones it shows, as the page lists them before ordering. */
const tabs = ["overview", "records", "submissions", "preview", "yaml", "runs", "schedules"].map((value) => ({ value }));

const values = (ordered: readonly { value: string }[]) => ordered.map((tab) => tab.value);

describe("orderPipelineTabs", () => {
  it("keeps the page's own order when the kind names none", () => {
    assert.deepEqual(values(orderPipelineTabs(tabs, undefined)), ["overview", "records", "submissions", "preview", "yaml", "runs", "schedules"]);
    assert.deepEqual(values(orderPipelineTabs(tabs, [])), ["overview", "records", "submissions", "preview", "yaml", "runs", "schedules"]);
  });

  it("places a built-in tab among the kind's own", () => {
    const order = ["overview", "records", "runs", "submissions", "schedules", "preview", "yaml"];
    assert.deepEqual(values(orderPipelineTabs(tabs, order)), order);
  });

  it("puts the tabs it names first and the rest after them as they stood", () => {
    assert.deepEqual(values(orderPipelineTabs(tabs, ["runs", "overview"])), ["runs", "overview", "records", "submissions", "preview", "yaml", "schedules"]);
  });

  it("passes over a name that is no tab of the page", () => {
    assert.deepEqual(values(orderPipelineTabs(tabs, ["files", "runs", "transforms"])), ["runs", "overview", "records", "submissions", "preview", "yaml", "schedules"]);
  });

  it("keeps a name given twice at its first place", () => {
    assert.deepEqual(values(orderPipelineTabs(tabs, ["preview", "runs", "preview"])).slice(0, 2), ["preview", "runs"]);
  });

  it("carries each tab as it was given and leaves the list it was given alone", () => {
    const given = [{ value: "yaml", label: "YAML" }, { value: "runs", label: "Runs" }];
    const ordered = orderPipelineTabs(given, ["runs"]);
    assert.equal(ordered[0], given[1]);
    assert.deepEqual(values(given), ["yaml", "runs"]);
  });
});
