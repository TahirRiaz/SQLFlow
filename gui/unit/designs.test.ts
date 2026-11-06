// The workbench's designs: what a module may add, which appearance a person wears from what their browser keeps, and the
// pre-paint script each index.html carries, which reads the same keys.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";
import {
  DESIGN_STORAGE_KEY,
  MODE_STORAGE_KEY,
  SQLFLOW_DESIGN,
  designById,
  installModuleDesigns,
  offeredDesigns,
  pickAppearance,
  resolveAppearance,
  type DesignContribution,
} from "../src/theme/designs.ts";

const ocean: DesignContribution = { id: "ocean", label: "Ocean" };

function install(...designs: DesignContribution[]): void {
  installModuleDesigns([{ moduleId: "m", designs }]);
}

describe("installModuleDesigns", () => {
  it("offers SQLFlow's design alone when no module adds one", () => {
    installModuleDesigns([]);
    assert.deepEqual(offeredDesigns().map((design) => design.id), ["sqlflow"]);
    assert.equal(designById("anything").id, "sqlflow");
  });

  it("offers a module's designs after SQLFlow's, in registration order", () => {
    installModuleDesigns([
      { moduleId: "a", designs: [ocean] },
      { moduleId: "b", designs: [{ id: "forest", label: "Forest", stylesheets: ["https://fonts.example/forest.css"] }] },
    ]);
    assert.deepEqual(offeredDesigns().map((design) => design.label), ["SQLFlow", "Ocean", "Forest"]);
    assert.deepEqual(designById("forest").stylesheets, ["https://fonts.example/forest.css"]);
    assert.equal(designById("ocean").marks, null);
    installModuleDesigns([]);
  });

  it("refuses an id that cannot name the design on <html> or in storage", () => {
    for (const id of ["", "Ocean", "1ocean", "ocean blue", "ocean_blue"]) {
      assert.throws(() => install({ id, label: "Ocean" }), /a design id is lowercase letters, digits and hyphens/);
    }
  });

  it("refuses SQLFlow's id, a second design under an id, and a label already taken", () => {
    assert.throws(() => install({ id: "sqlflow", label: "Mine" }), /'sqlflow', which SQLFlow already adds/);
    assert.throws(
      () => installModuleDesigns([{ moduleId: "a", designs: [ocean] }, { moduleId: "b", designs: [{ id: "ocean", label: "Sea" }] }]),
      /GUI module 'b' adds the design 'ocean', which module 'a' already adds/,
    );
    assert.throws(() => install({ id: "mine", label: "sqlflow" }), /which SQLFlow's design is already called/);
    assert.throws(() => install(ocean, { id: "sea", label: " OCEAN " }), /which a design of module 'm' is already called/);
    assert.throws(() => install({ id: "blank", label: "  " }), /with a blank label/);
  });

  it("takes stylesheets only by absolute https URL", () => {
    assert.throws(() => install({ ...ocean, stylesheets: ["/fonts/ocean.css"] }), /which is not an absolute URL/);
    assert.throws(() => install({ ...ocean, stylesheets: ["http://fonts.example/ocean.css"] }), /which is not served over https/);
  });

  it("takes a mark for each mode, and a crop only inside its image", () => {
    const mark = { src: "https://cdn.example/logo.svg", crop: { imageWidth: 100, imageHeight: 80, x: 60, y: 0, width: 40, height: 50 } };
    install({ ...ocean, marks: { light: mark, dark: { src: "/brand/ocean-white.svg" } } });
    assert.deepEqual(designById("ocean").marks?.light, mark);
    assert.throws(
      () => install({ ...ocean, marks: { light: mark } as unknown as DesignContribution["marks"] }),
      /a dark title bar mark with no image/,
    );
    assert.throws(
      () => install({ ...ocean, marks: { light: { ...mark, crop: { ...mark.crop, x: 61 } }, dark: mark } }),
      /runs past the image \(100 by 80\)/,
    );
    assert.throws(
      () => install({ ...ocean, marks: { light: { ...mark, crop: { ...mark.crop, width: 0 } }, dark: mark } }),
      /whose crop is not a box/,
    );
    installModuleDesigns([]);
  });

  it("lets one design, across the modules, start people in it, in a light or dark mode", () => {
    assert.throws(
      () => installModuleDesigns([
        { moduleId: "a", designs: [{ ...ocean, firstMode: "dark" }] },
        { moduleId: "b", designs: [{ id: "forest", label: "Forest", firstMode: "light" }] },
      ]),
      /GUI module 'b' starts people in the design 'forest', and module 'a' already starts them in another/,
    );
    assert.throws(
      () => install({ ...ocean, firstMode: "dim" as unknown as "dark" }),
      /in the mode 'dim'; the modes are light and dark/,
    );
  });
});

describe("pickAppearance", () => {
  const designs = [SQLFLOW_DESIGN, { id: "ocean" }];
  const oceanDark = { design: "ocean", mode: "dark" as const };

  it("puts on what the browser kept, while its design is offered", () => {
    assert.deepEqual(pickAppearance({ design: "sqlflow", mode: "light" }, true, designs, oceanDark), {
      appearance: { design: "sqlflow", mode: "light" },
      followsSystem: false,
    });
    assert.deepEqual(pickAppearance({ design: "ocean", mode: "light" }, false, designs, null).appearance, { design: "ocean", mode: "light" });
  });

  it("starts a first visit in the design and mode a module starts people in, whatever the system prefers", () => {
    assert.deepEqual(pickAppearance({ design: null, mode: null }, false, designs, oceanDark), { appearance: oceanDark, followsSystem: false });
  });

  it("keeps a mode a person chose before designs were offered, in the design a module starts people in", () => {
    assert.deepEqual(pickAppearance({ design: null, mode: "light" }, true, designs, oceanDark).appearance, { design: "ocean", mode: "light" });
  });

  it("falls back from a design no longer offered, keeping the mode", () => {
    assert.deepEqual(pickAppearance({ design: "retired", mode: "light" }, true, designs, oceanDark).appearance, {
      design: "ocean",
      mode: "light",
    });
    assert.deepEqual(pickAppearance({ design: "retired", mode: "dark" }, false, designs, null).appearance, { design: "sqlflow", mode: "dark" });
  });

  it("follows the system's mode, live and unkept, in SQLFlow's design when nothing is kept or started", () => {
    assert.deepEqual(pickAppearance({ design: null, mode: null }, true, designs, null), {
      appearance: { design: "sqlflow", mode: "dark" },
      followsSystem: true,
    });
    assert.deepEqual(pickAppearance({ design: null, mode: "dim" }, false, designs, null), {
      appearance: { design: "sqlflow", mode: "light" },
      followsSystem: true,
    });
  });

  it("follows the system's mode in a kept design other than the one a module starts people in", () => {
    assert.deepEqual(pickAppearance({ design: "sqlflow", mode: null }, true, designs, oceanDark), {
      appearance: { design: "sqlflow", mode: "dark" },
      followsSystem: true,
    });
  });

  it("resolves over the installed designs", () => {
    install({ ...ocean, firstMode: "dark" });
    assert.deepEqual(resolveAppearance({ design: null, mode: null }, false).appearance, oceanDark);
    installModuleDesigns([]);
    assert.deepEqual(resolveAppearance({ design: "ocean", mode: null }, false).appearance, { design: "sqlflow", mode: "light" });
  });
});

describe("index.html", () => {
  it("puts on, before the first paint, the appearance kept under the keys the workbench keeps it under", () => {
    const page = readFileSync(new URL("../index.html", import.meta.url), "utf8");
    assert.ok(page.includes(`localStorage.getItem("${DESIGN_STORAGE_KEY}")`), "the design's key");
    assert.ok(page.includes(`localStorage.getItem("${MODE_STORAGE_KEY}")`), "the mode's key");
    assert.ok(page.includes('root.setAttribute("data-sf-design", design)'), "the design's attribute");
    assert.ok(page.indexOf("<script>") < page.indexOf("</head>"), "the script runs in the head, before the body paints");
  });
});
