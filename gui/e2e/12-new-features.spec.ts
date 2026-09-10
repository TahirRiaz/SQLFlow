import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "./helpers";

// The features added on top of the read-only operate GUI: the preview-first scan (discover a repo's flows without
// importing), the project grouping on a repo (root folder = a source's pipelines), and schedule catch-up. These run
// after the seed (03), so the fixture repo is already synced as "e2e-repo" with its Csv_Basic pipeline.

function fixtureMeta(): { repoDir: string; headSha: string } {
  const metaPath = join(import.meta.dirname, ".fixtures", "meta.json");
  return JSON.parse(readFileSync(metaPath, "utf8")) as { repoDir: string; headSha: string };
}

test.describe.serial("new features", () => {
  test("discover previews a repo's flows without importing", async ({ adminPage }) => {
    const meta = fixtureMeta();
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("open-register-source").click();

    // Fill the remote and discover: a read-only clone + parse, nothing written to the catalog.
    await adminPage.getByTestId("source-remote-url").fill(meta.repoDir);
    await adminPage.getByTestId("source-branch").fill("main");
    await adminPage.getByTestId("discover-flows").click();

    // The fixture's single flow appears with its path; parse succeeded (no error chip), and preview opens.
    const flows = adminPage.getByTestId("discovered-flows");
    await expect(flows).toBeVisible({ timeout: 60_000 });
    const row = adminPage.getByTestId("discovered-flow-row").filter({ hasText: "csv-basic.flow.yaml" });
    await expect(row.first()).toBeVisible();
    await expect(row.first().getByTestId("discovered-flow-error")).toHaveCount(0);
    await row.first().getByTestId("discovered-flow-preview").click();

    // Cancel: discover is preview-only, so no second source is registered.
    await adminPage.getByTestId("register-source-cancel").click();
    await expect(adminPage.getByTestId("register-source-dialog")).toHaveCount(0);
  });

  test("the synced repo shows its pipelines grouped by project", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible({ timeout: 15_000 });

    // Pipelines are grouped into project (root-folder) sections; the fixture flow sits at the repo root, so it is
    // under one project accordion. The accordions start collapsed, so opening the "(root)" one reveals its flows.
    await expect(adminPage.getByTestId("repo-projects")).toBeVisible({ timeout: 15_000 });
    const rootFolder = adminPage.getByTestId("repo-project").filter({ hasText: "(root)" });
    await expect(rootFolder).toBeVisible();
    await rootFolder.getByText("(root)").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first()).toBeVisible();
  });

  test("a catch-up schedule shows the catchup chip", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    await expect(adminPage.getByTestId("page-schedules")).toBeVisible();

    await adminPage.getByTestId("open-create-schedule").click();
    await adminPage.getByTestId("schedule-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("schedule-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    // A far-out interval so it never actually fires during the suite, with catch-up on.
    await adminPage.getByTestId("schedule-trigger-interval").click();
    await adminPage.getByTestId("schedule-interval").fill("21600");
    await adminPage.getByTestId("schedule-catchup").click();
    await adminPage.getByTestId("create-schedule-submit").click();

    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row.getByText("catchup")).toBeVisible();

    // Clean up so the suite leaves no schedule behind.
    await row.getByTestId("schedule-delete").click();
    await expect(adminPage.getByTestId("confirm-dialog")).toBeVisible();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }))
      .toHaveCount(0, { timeout: 15_000 });
  });
});
