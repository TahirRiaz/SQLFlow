import { expect, test } from "./helpers";

// The explore surface: lineage objects and the drawer, the dependency graph, and search with its deep links.
// The seeded estate is one file flow, so object/lineage volume is small but real (the sync registers the flow's
// target objects and edges).

test.describe("explore", () => {
  test("the explorer lists objects or a clean empty state; the drawer opens on a row", async ({ adminPage }) => {
    // Lineage lands on the graph; the searchable object catalog is the Explorer, one click away.
    await adminPage.getByTestId("nav-lineage").click();
    await expect(adminPage.getByTestId("page-lineage-graph")).toBeVisible();
    await adminPage.getByTestId("open-lineage-objects").click();
    await expect(adminPage.getByTestId("page-lineage")).toBeVisible();

    const firstRow = adminPage.getByTestId("table-row").first();
    const empty = adminPage.getByTestId("empty-message");
    await expect(firstRow.or(empty)).toBeVisible({ timeout: 30_000 });

    if (await firstRow.isVisible()) {
      await firstRow.click();
      await expect(adminPage.getByTestId("object-drawer")).toBeVisible();
      await adminPage.keyboard.press("Escape");
    }
  });

  test("lineage lands on the graph, and the explorer filters work", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await expect(adminPage.getByTestId("page-lineage-graph")).toBeVisible();
    await expect(adminPage.getByTestId("graph-empty")).toBeVisible();

    await adminPage.getByTestId("open-lineage-objects").click();
    await expect(adminPage.getByTestId("page-lineage")).toBeVisible();
    await adminPage.getByTestId("filter-object-name").fill("no-such-object-zzz");
    await expect(adminPage.getByTestId("empty-message")).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-object-name").fill("");

    // The Graph button returns to the graph landing.
    await adminPage.getByTestId("open-lineage-graph").click();
    await expect(adminPage.getByTestId("page-lineage-graph")).toBeVisible();
  });

  test("the flows graph focuses on click and opens the pipeline from the panel", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await adminPage.getByTestId("graph-project-select").click();
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();

    const node = adminPage.locator(".react-flow__node").first();
    const noLineage = adminPage.getByTestId("graph-no-lineage");
    await expect(node.or(noLineage)).toBeVisible({ timeout: 60_000 });

    if (await node.isVisible()) {
      await expect(adminPage.getByTestId("wave-list")).toBeVisible();

      // Click focuses (upstream/downstream trace) instead of leaving the page; the panel opens the pipeline.
      await node.click();
      await expect(adminPage.getByTestId("graph-focus-panel")).toBeVisible();
      await expect(adminPage.getByTestId("graph-focus-panel")).toContainText("upstream");

      await adminPage.getByTestId("graph-clear-focus").click();
      await expect(adminPage.getByTestId("graph-focus-panel")).toHaveCount(0);

      // The node search finds and focuses a node by name. Exact match: the flows view also lists a pipeline's
      // terminal output tables as nodes (e.g. Csv_Basic_E2E_*), so a substring match would be ambiguous.
      await adminPage.getByTestId("graph-node-search").fill("Csv");
      await adminPage.getByRole("option", { name: "Csv_Basic", exact: true }).click();
      await expect(adminPage.getByTestId("graph-focus-panel")).toBeVisible();

      await adminPage.getByTestId("graph-open-selected").click();
      await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 15_000 });
    }
  });

  test("the toolbar search reaches the whole catalog and seeds the graph on the hit", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await expect(adminPage.getByTestId("graph-empty")).toBeVisible();

    // No project is chosen, so no node is drawn: every hit here comes from the catalog search, which is the point
    // (the canvas holds one project, the estate holds everything).
    await adminPage.getByTestId("graph-node-search").fill("Csv_Basic");
    const flowHit = adminPage.getByTestId("graph-search-flow").first();
    await expect(flowHit).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("graph-search-object").first()).toBeVisible();

    // Picking a hit re-seeds the graph on that node: it becomes ?focus= and lands focused. The user got there
    // without first having to guess which project the flow lives in.
    await flowHit.click();
    await expect(adminPage.getByTestId("graph-focus-panel")).toBeVisible({ timeout: 60_000 });
    expect(new URL(adminPage.url()).searchParams.get("focus")).not.toBeNull();
  });

  test("the objects view draws the data flow between file and table, colored per flow", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await adminPage.getByTestId("graph-project-select").click();
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("graph-view-objects").click();

    // The seeded csv flow reads a file object and writes a table object: at least two nodes and an edge.
    const node = adminPage.locator(".react-flow__node").first();
    const noLineage = adminPage.getByTestId("graph-no-lineage");
    await expect(node.or(noLineage)).toBeVisible({ timeout: 60_000 });

    if (await node.isVisible()) {
      expect(await adminPage.locator(".react-flow__node").count()).toBeGreaterThanOrEqual(2);
      await expect(adminPage.locator(".react-flow__edge").first()).toBeVisible();
      await expect(adminPage.getByTestId("graph-flow-legend")).toContainText("Csv_Basic");

      // Focusing an object shows its trace and deep-links into the explorer.
      await node.click();
      await expect(adminPage.getByTestId("graph-focus-panel")).toBeVisible();
      await adminPage.getByTestId("graph-open-selected").click();
      await expect(adminPage.getByTestId("page-lineage")).toBeVisible({ timeout: 15_000 });

      // ...and the explorer's Graph view button comes back to that same drawing, scope and view intact. The graph
      // rewrites its query string in place, so without the remembered parameters the return lands on a blank map.
      await adminPage.getByTestId("open-lineage-graph").click();
      await expect(adminPage.getByTestId("page-lineage-graph")).toBeVisible();
      const returned = new URL(adminPage.url()).searchParams;
      expect(returned.get("repoId")).not.toBeNull();
      expect(returned.get("project")).not.toBeNull();
      expect(returned.get("view")).toBe("objects");
      await expect(adminPage.locator(".react-flow__node").first()).toBeVisible({ timeout: 60_000 });
    }
  });

  test("global search runs across every surface and deep-links into lineage", async ({ adminPage }) => {
    await adminPage.getByTestId("global-search").fill("Csv_Basic");
    await adminPage.getByTestId("global-search").press("Enter");
    await expect(adminPage.getByTestId("page-search")).toBeVisible();

    // The landing tab is the unified All view: one query fanned across objects, columns, code, files, and flows.
    await expect(
      adminPage.getByTestId("search-all").or(adminPage.getByTestId("search-all-empty")),
    ).toBeVisible({ timeout: 30_000 });

    const tabs = adminPage.getByTestId("search-tabs");
    for (const tab of [/^objects$/i, /^columns$/i, /^definitions$/i, /^files$/i, /^flows$/i]) {
      await tabs.getByRole("tab", { name: tab }).click();
      await expect(
        adminPage.getByTestId("table-row").first().or(adminPage.getByTestId("empty-message")),
      ).toBeVisible({ timeout: 30_000 });
    }

    // An empty query never fires the (400-on-empty) endpoints; the hint renders instead.
    await adminPage.getByTestId("search-input").fill("");
    await adminPage.getByTestId("search-submit").click();
    await expect(adminPage.getByTestId("search-hint")).toBeVisible();
  });
});
