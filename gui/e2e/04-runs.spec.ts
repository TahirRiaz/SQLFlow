import { expect, test } from "./helpers";

// The run lifecycle end to end from the GUI: trigger, watch the in-process worker execute it, drill into every
// detail tab, exercise the filters, and cancel a queued run (kept queued by routing it to a pool no node serves).

test.describe.serial("runs", () => {
  test("trigger a run and watch it succeed with populated drill-downs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await expect(adminPage.getByTestId("page-runs")).toBeVisible();

    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();
    await adminPage.getByTestId("trigger-submit").click();

    // 202 accepted: the dialog navigates to the run detail, which polls to a terminal state.
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });

    // Every drill-down tab renders; statements carry the generated SQL (this suite's table is unique per run,
    // so the first trigger CREATEs it and the DDL statement is recorded) and open the full-statement dialog.
    await expect(async () => {
      await adminPage.reload();
      await expect(adminPage.getByTestId("run-tabs")).toBeVisible({ timeout: 10_000 });
      await adminPage.getByTestId("run-tabs").getByRole("tab", { name: /statements/i }).click();
      await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    const statementRow = adminPage.getByTestId("table-row").first();
    await statementRow.click();
    await expect(adminPage.getByTestId("statement-dialog")).toBeVisible();
    await adminPage.keyboard.press("Escape");

    for (const tab of ["assertions", "files", "surrogate-keys", "health-metrics"]) {
      await adminPage.getByTestId(`tab-${tab}`).click();
      await expect(adminPage.getByTestId(`${tab}-table`)).toBeVisible();
    }
  });

  test("the runs list shows the run and the status filter narrows it", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" });
    await expect(row.first()).toBeVisible({ timeout: 30_000 });

    await adminPage.getByTestId("filter-status").click();
    await adminPage.getByRole("option", { name: "succeeded" }).click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first()).toBeVisible();

    await adminPage.getByTestId("filter-status").click();
    await adminPage.getByRole("option", { name: "failed" }).click();
    await expect(
      adminPage.getByTestId("empty-message")
        .or(adminPage.getByTestId("table-row").filter({ hasText: "failed" }).first()),
    ).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-status").click();
    await adminPage.getByRole("option", { name: "all statuses" }).click(); // back to all
  });

  test("runs group under their schedule, collapsed by default, then batch and step; the batch dropdown filters", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await expect(adminPage.getByTestId("page-runs")).toBeVisible();

    // The grouped board nests schedule -> batch -> step, and the schedule (top) level starts COLLAPSED: the
    // schedule headers show, but their subtrees (batch and step nodes and leaf rows) are hidden until drilled
    // into. Csv_Basic declares no batch and joins no schedule, so it lives under the Unscheduled schedule group.
    await expect(adminPage.getByTestId("schedule-group-header").first()).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("table-row")).toHaveCount(0);
    await expect(adminPage.getByTestId("subgroup-header-row")).toHaveCount(0);

    // Every level starts collapsed, so you drill in one level at a time: opening a node reveals only its
    // immediate children, themselves collapsed. Schedule -> batch nodes (no steps, no rows yet).
    const schedules = adminPage.getByTestId("group-header-row");
    await schedules.first().click();
    await expect(adminPage.getByTestId("batch-group-header").first()).toBeVisible();
    await expect(adminPage.getByTestId("step-group-header")).toHaveCount(0);
    await expect(adminPage.getByTestId("table-row")).toHaveCount(0);

    // Batch -> step nodes (still no rows).
    const firstBatch = adminPage.getByTestId("batch-group-header").first();
    await firstBatch.click();
    await expect(adminPage.getByTestId("step-group-header").first()).toBeVisible();
    await expect(adminPage.getByTestId("table-row")).toHaveCount(0);

    // Step -> the run rows.
    const firstStep = adminPage.getByTestId("step-group-header").first();
    await firstStep.click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible();

    // A node collapses independently of its ancestors: collapsing the open step hides its runs while the batch
    // and schedule above it stay open; re-opening it brings the runs back.
    await firstStep.click();
    await expect(adminPage.getByTestId("table-row")).toHaveCount(0);
    await expect(adminPage.getByTestId("batch-group-header").first()).toBeVisible();
    await firstStep.click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible();

    // Ungrouped, batch and step become ordinary columns, the header rows disappear, and rows show flat.
    await adminPage.getByTestId("group-by-batch").click();
    await expect(adminPage.getByTestId("group-header-row")).toHaveCount(0);
    await expect(adminPage.getByRole("columnheader", { name: "Batch" })).toBeVisible();
    await expect(adminPage.getByRole("columnheader", { name: "Step" })).toBeVisible();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible();

    // The batch filter is a searchable dropdown of the estate's real batch labels: selecting "default" narrows
    // the flat list to that batch; clearing it restores the full list.
    await adminPage.getByTestId("filter-batch").click();
    await adminPage.getByPlaceholder("Search batches").fill("default");
    await adminPage.getByRole("option", { name: /default/ }).first().click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-batch").click();
    await adminPage.getByRole("option", { name: "Clear filter" }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 15_000 });

    await adminPage.getByTestId("group-by-batch").click(); // back to the grouped default
    await expect(adminPage.getByTestId("schedule-group-header").first()).toBeVisible();

    // The history scope defaults to "Last run" (each flow's newest run); "All" opens the full history. Toggling
    // stays a single active choice.
    await expect(adminPage.getByTestId("filter-view-last")).toHaveAttribute("data-state", "on");
    await adminPage.getByTestId("filter-view-all").click();
    await expect(adminPage.getByTestId("filter-view-all")).toHaveAttribute("data-state", "on");
    await expect(adminPage.getByTestId("filter-view-last")).toHaveAttribute("data-state", "off");
    await adminPage.getByTestId("filter-view-last").click();
    await expect(adminPage.getByTestId("filter-view-last")).toHaveAttribute("data-state", "on");
  });

  test("a queued run (pooled to no node) can be cancelled from its detail page", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();
    // No worker serves this pool, so the run stays queued and the cancel path is deterministic.
    await adminPage.getByTestId("trigger-pool").fill("e2e-unserved-pool");
    await adminPage.getByTestId("trigger-submit").click();

    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "queued" }).first()).toBeVisible();
    await adminPage.getByTestId("cancel-run").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "cancelled" }).first())
      .toBeVisible({ timeout: 30_000 });
    // A terminal run offers no cancel affordance.
    await expect(adminPage.getByTestId("cancel-run")).toHaveCount(0);

    // A terminal run offers a re-run instead. The re-run repeats the run's own parameters, including the
    // unserved pool, so the new run also stays queued; the page navigates to it. Cancel it to clean up.
    const cancelledUrl = adminPage.url();
    await adminPage.getByTestId("rerun-run").click();
    await expect(adminPage).not.toHaveURL(cancelledUrl, { timeout: 15_000 });
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible();
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "queued" }).first())
      .toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("cancel-run").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "cancelled" }).first())
      .toBeVisible({ timeout: 30_000 });
  });

  test("run detail links back to its pipeline and repo", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible();
    await adminPage.getByTestId("run-pipeline-link").click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 15_000 });
  });
});
