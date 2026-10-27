// How a module's layout of the whole menu places the entries, and what it refuses: every entry is listed once, where the
// layout says, and nothing it gets wrong renders as a half-built menu.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { layOutMenu, type MenuGroupLayout, type MenuLayout } from "../src/layout/menuLayout.ts";

interface Entry {
  to: string;
  label: string;
  section?: string;
}

const entry = (to: string): Entry => ({ to, label: to.slice(1) || "home" });

const ENTRIES: Entry[] = ["/", "/runs", "/records", "/mappings", "/pipelines", "/tokens"].map(entry);

const fail = (message: string): never => {
  throw new Error(message);
};

const lay = (groups: MenuGroupLayout<string>[], entries: readonly Entry[] = ENTRIES) =>
  layOutMenu(entries, { groups } satisfies MenuLayout<string>, fail);

const MENU: MenuGroupLayout<string>[] = [
  { id: "operate", label: "Operate", icon: "activity", entries: ["/", "/runs"] },
  {
    id: "build",
    label: "Build",
    icon: "blocks",
    entries: ["/records"],
    sections: [
      { id: "flows", label: "Flows", entries: ["/pipelines"] },
      { id: "delivery", label: "Delivery", entries: ["/mappings"] },
    ],
  },
  { id: "settings", label: "Settings", icon: "gear", bottom: true, entries: ["/tokens"] },
];

describe("layOutMenu", () => {
  it("lists each group's entries in the layout's order, those of no section first, each carrying its section", () => {
    const groups = lay(MENU);

    assert.deepEqual(groups.map((laid) => laid.group.id), ["operate", "build", "settings"]);
    assert.deepEqual(groups[0].items.map((item) => [item.to, item.section]), [["/", undefined], ["/runs", undefined]]);
    assert.deepEqual(groups[1].items.map((item) => [item.to, item.section]), [
      ["/records", undefined],
      ["/pipelines", "flows"],
      ["/mappings", "delivery"],
    ]);
    assert.deepEqual(groups[1].sections, [{ id: "flows", label: "Flows" }, { id: "delivery", label: "Delivery" }]);
    assert.deepEqual(groups[2].sections, []);
    assert.equal(groups[2].group.bottom, true);
  });

  it("keeps everything else an entry says, and leaves the entries it was given as they were", () => {
    const given = ENTRIES.map((item) => (item.to === "/pipelines" ? { ...item, section: "somewhere" } : item));
    const placed = lay(MENU, given).flatMap((laid) => laid.items);

    assert.deepEqual(placed.find((item) => item.to === "/mappings"), { to: "/mappings", label: "mappings", section: "delivery" });
    assert.equal(given.find((item) => item.to === "/pipelines")?.section, "somewhere");
    assert.equal(given.find((item) => item.to === "/mappings")?.section, undefined);
  });

  it("refuses an entry the layout leaves out, naming every one", () => {
    const withoutRuns = MENU.map((group) => (group.id === "operate" ? { ...group, entries: ["/"] } : group));
    assert.throws(() => lay(withoutRuns), { message: "leaves the navigation entry '/runs' out of its menu." });

    const extra = [...ENTRIES, entry("/new"), entry("/newer")];
    assert.throws(() => lay(MENU, extra), { message: "leaves the navigation entries '/new', '/newer' out of its menu." });
  });

  it("refuses a path listed twice, or one no entry has", () => {
    const twice = MENU.map((group) => (group.id === "settings" ? { ...group, entries: ["/tokens", "/runs"] } : group));
    assert.throws(() => lay(twice), { message: "lists the navigation entry '/runs' twice, the second time in the group 'settings'." });

    const unknown = MENU.map((group) => (group.id === "operate" ? { ...group, entries: ["/", "/runs", "/gone"] } : group));
    assert.throws(() => lay(unknown), { message: "lists '/gone' in the group 'operate', which is not the path of a navigation entry." });
  });

  it("refuses a group laid out twice, or one without an id or a label", () => {
    assert.throws(() => lay([...MENU, { id: "operate", label: "Again", icon: "x", bottom: true, entries: [] }]), {
      message: "lays out the navigation group 'operate' twice.",
    });
    for (const blank of [{ id: " ", label: "Operate" }, { id: "operate", label: "" }]) {
      assert.throws(() => lay([{ ...MENU[0], ...blank }, ...MENU.slice(1)]), {
        message: "lays out a navigation group without an id or a label.",
      });
    }
  });

  it("refuses a section laid out twice in its group, one without an id or a label, and one listing nothing", () => {
    const build = MENU[1];
    const withSections = (sections: MenuGroupLayout<string>["sections"]) => MENU.map((group) => (group === build ? { ...group, sections } : group));

    assert.throws(() => lay(withSections([
      { id: "flows", label: "Flows", entries: ["/pipelines"] },
      { id: "flows", label: "Again", entries: ["/mappings"] },
    ])), { message: "lays out the section 'flows' of the navigation group 'build' twice." });
    assert.throws(() => lay(withSections([
      { id: "flows", label: " ", entries: ["/pipelines", "/mappings"] },
    ])), { message: "lays out a section of the navigation group 'build' without an id or a label." });
    assert.throws(() => lay(withSections([
      { id: "flows", label: "Flows", entries: ["/pipelines", "/mappings"] },
      { id: "empty", label: "Empty", entries: [] },
    ])), { message: "lays out the section 'empty' of the navigation group 'build' with no entry." });
  });

  it("takes one section id in two groups, as each group's own", () => {
    const groups = lay([
      { id: "a", label: "A", icon: "a", sections: [{ id: "main", label: "Main", entries: ["/", "/runs", "/records"] }] },
      { id: "b", label: "B", icon: "b", sections: [{ id: "main", label: "Main", entries: ["/mappings", "/pipelines", "/tokens"] }] },
    ]);

    assert.deepEqual(groups.map((laid) => laid.items.map((item) => item.section)), [["main", "main", "main"], ["main", "main", "main"]]);
  });

  it("refuses a group listing nothing", () => {
    assert.throws(() => lay([...MENU.slice(0, 2), { id: "empty", label: "Empty", icon: "x" }, MENU[2]]), {
      message: "lays out the navigation group 'empty' with no entry.",
    });
  });

  it("keeps the bottom groups after every top group, as the activity bar shows them", () => {
    const groups = lay([
      { id: "operate", label: "Operate", icon: "a", entries: ["/", "/runs", "/records", "/mappings", "/pipelines"] },
      { id: "admin", label: "Admin", icon: "b", bottom: true, entries: ["/tokens"] },
    ]);
    assert.deepEqual(groups.map((laid) => laid.group.id), ["operate", "admin"]);

    assert.throws(() => lay([MENU[2], MENU[0], MENU[1]]), {
      message: "lays out the top group 'operate' after the bottom group 'settings'.",
    });
  });
});
