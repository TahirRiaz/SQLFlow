import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
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
    await expect(adminPage.getByTestId("repo-pipeline").filter({ hasText: "Csv_Basic" }).first()).toBeVisible();
  });

  test("a project lists its contents as a folder tree, not as one flat list of paths", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible({ timeout: 15_000 });

    // The fixture keeps its CSV under data/, and an older copy under data/archive/2026/, so the outline has real
    // depth to show. The data project holds no flow, so it starts collapsed: opening it reveals the folder itself
    // rather than the file paths flattened into rows.
    const dataProject = adminPage.getByTestId("repo-project").filter({ hasText: "data" }).first();
    await expect(dataProject).toBeVisible({ timeout: 15_000 });
    await dataProject.getByText("data", { exact: true }).click();

    // One row per folder, nested: archive holds 2026, which holds the file. Each level has to be opened, which is
    // what tells the folders apart from a flat listing that merely prints slashes inside a path.
    const archive = dataProject.getByTestId("repo-folder").filter({ hasText: "archive" });
    await expect(archive).toBeVisible();
    await expect(dataProject.getByTestId("repo-file").filter({ hasText: "orders-2026.csv" })).toHaveCount(0);
    await archive.click();
    const year = dataProject.getByTestId("repo-folder").filter({ hasText: "2026" });
    await expect(year).toBeVisible();
    await year.click();

    // The leaf carries its own name, not the whole path: the folders above it already said where it is.
    const nested = dataProject.getByTestId("repo-file").filter({ hasText: "orders-2026.csv" });
    await expect(nested).toBeVisible();
    await expect(nested).not.toContainText("data/archive");

    // And the tree is a tree to the keyboard too, which is what the shared workbench primitive buys.
    await expect(dataProject.getByRole("tree")).toHaveCount(1);
    await expect(archive).toHaveAttribute("aria-expanded", "true");
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

  /**
   * The repository decides what a repo holds. A rename lands on the branch at once while the catalog only moves when
   * a sync runs, so between the two the catalog still lists a pipeline whose file is gone. It must not be drawn: with
   * the file that replaced it listed beside it, one flow would appear twice, once under a name it no longer has.
   *
   * The window is made deterministic rather than waited for. The source is registered with a sync interval no suite
   * run can cross, and pulled once by hand; the rename that follows therefore cannot be picked up until this test
   * says so. The source is its own, so nothing here disturbs the estate the other specs run against.
   */
  test("a pipeline whose file left the branch is not listed", async ({ adminPage }) => {
    const repoDir = join(mkdtempSync(join(tmpdir(), "sqlflow-branch-")), "repo");
    const git = (...args: string[]) => execFileSync("git", args, { cwd: repoDir, stdio: "pipe" });
    mkdirSync(join(repoDir, "flows"), { recursive: true });
    const flow = readFileSync(join(fixtureMeta().repoDir, "csv-basic.flow.yaml"), "utf8")
      .replace(/^name:.*$/m, "name: Branch_Truth");
    writeFileSync(join(repoDir, "flows", "branch-truth.flow.yaml"), flow);
    git("init", "-b", "main");
    git("config", "user.email", "e2e@sqlflow.test");
    git("config", "user.name", "SQLFlow E2E");
    git("add", "-A");
    git("commit", "-m", "one flow");

    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("open-register-source").click();
    await adminPage.getByTestId("source-name").fill("e2e-branch");
    await adminPage.getByTestId("source-remote-url").fill(repoDir.replace(/\\/g, "/"));
    // Far longer than any suite run, so the managed sync cannot fire between the rename below and the assertions.
    await adminPage.getByTestId("source-interval").fill("86400");
    await adminPage.getByTestId("register-source-submit").click();

    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-branch" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByTestId("source-sync-now").click();

    // The one pull this source ever gets: the flow is now in the catalog, at the path it was committed under.
    await row.click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("repo-pipeline").filter({ hasText: "Branch_Truth" }).first())
      .toBeVisible({ timeout: 60_000 });

    // The branch moves and the catalog does not.
    git("mv", "flows/branch-truth.flow.yaml", "flows/branch-truth-renamed.flow.yaml");
    git("commit", "-m", "rename the flow");
    await adminPage.reload();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible({ timeout: 15_000 });

    // The stale pipeline is gone from the outline, the file that replaced it is listed as a file, and the reader is
    // told why rather than left to wonder where the flow went.
    await expect(adminPage.getByTestId("repo-file").filter({ hasText: "branch-truth-renamed.flow.yaml" }).first())
      .toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("repo-pipeline").filter({ hasText: "Branch_Truth" })).toHaveCount(0);
    await expect(adminPage.getByTestId("repo-pipelines-not-on-branch")).toContainText("Branch_Truth");

    // This source is this test's own; it leaves with it.
    await adminPage.getByTestId("nav-repos").click();
    const registered = adminPage.getByTestId("table-row").filter({ hasText: "e2e-branch" }).first();
    await registered.getByTestId("repo-delete-open").click();
    await adminPage.getByTestId("repo-delete-confirm-input").fill("e2e-branch");
    await adminPage.getByTestId("repo-delete-confirm-button").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "e2e-branch" })).toHaveCount(0, { timeout: 30_000 });
  });

});
