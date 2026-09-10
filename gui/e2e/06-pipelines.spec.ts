import { expect, test } from "./helpers";

// The pipeline catalog: list filters, the read-only YAML and definition views, the runs/schedules tabs, and
// triggering with the pipeline prefilled.

test.describe.serial("pipelines", () => {
  test("search narrows the folder tree and clears again", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();

    await adminPage.getByTestId("filter-name").fill("Csv_Basic");
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first())
      .toBeVisible({ timeout: 15_000 });

    await adminPage.getByTestId("filter-name").fill("no-such-pipeline-name");
    await expect(adminPage.getByTestId("pipelines-no-matches")).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-name").fill("");
  });

  test("the repo filter scopes to one repo's project folders", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();

    // Scope to the fixture repo; a single-repo selection drops the repo level and lists its project folders directly.
    await adminPage.getByRole("combobox", { name: "Repo" }).click();
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();

    // The fixture flow sits at the repo root, so it groups under the "(root)" project folder. The folders start
    // collapsed, so open the "(root)" folder to reveal its flows.
    const rootFolder = adminPage.getByTestId("repo-project").filter({ hasText: "(root)" });
    await expect(rootFolder).toBeVisible({ timeout: 15_000 });
    await rootFolder.getByText("(root)").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first())
      .toBeVisible({ timeout: 15_000 });
  });

  test("pipeline detail shows YAML, definition, runs, and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill("Csv_Basic");
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText("Csv_Basic").first())
      .toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /definition/i }).click();
    await expect(adminPage.getByTestId("pipeline-definition")).toBeVisible();

    await tabs.getByRole("tab", { name: /runs/i }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /schedules/i }).click();
    await expect(adminPage.getByTestId("paged-table").first()).toBeVisible();
  });

  test("the header profiles the size of the flow's file deliveries", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill("Csv_Basic");
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    // Every flow carries the profile cell in its header, so the layout does not shift by flow kind.
    await expect(adminPage.getByText("Avg file size")).toBeVisible();

    // The fixture flow has loaded its CSV, so the Files tab lists it and the header reads a real byte size
    // (the average is computed from the same files the tab browses).
    await adminPage.getByTestId("pipeline-tab-files").click();
    await expect(adminPage.getByTestId("pipeline-files-table").getByTestId("table-row").first())
      .toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("pipeline-avg-file-size"))
      .toHaveText(/^\d+(\.\d+)? (B|KiB|MiB|GiB|TiB)$/, { timeout: 15_000 });
  });

  test("trigger from the pipeline detail is prefilled and lands on the run", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill("Csv_Basic");
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    // A flow-locked launch must still resolve its own run parameters: a file flow honors full load, so the toggle
    // is present and the dialog never claims the flow has none. Guards the launching context that prefills the
    // flow without its pipeline id, where the parameter lookup previously never fired.
    await expect(adminPage.getByTestId("trigger-fullLoad")).toBeVisible();
    await expect(adminPage.getByTestId("trigger-parameters-unavailable")).toHaveCount(0);
    // Repo and flow are locked by the pipeline context; just submit.
    await adminPage.getByTestId("trigger-submit").click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: /succeeded|running|queued/ }).first())
      .toBeVisible();
  });
});
