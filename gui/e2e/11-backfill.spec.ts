import { expect, test } from "./helpers";

// The built-in backfill through the GUI: the trigger dialog's run-parameters section carries per-run substitution
// parameters, the client refuses an impossible window, and a triggered backfill is audited on the run detail.
// The seeded fixture is a file flow (Csv_Basic), so a file-date window and pattern are the natural exercise.
// The controls are rendered from the flow's applicable parameters, so each test waits on the control itself
// rather than on a container: its presence IS the assertion that the lookup resolved.

test.describe.serial("backfill", () => {
  test("the dialog refuses full load combined with a window", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    // With full load on the window fields disable, so the conflict is reached by setting the window first and
    // toggling full load after it.
    await adminPage.getByTestId("trigger-backfill-from").fill("2023-01-01T00:00");
    await adminPage.getByTestId("trigger-fullLoad").check();
    await expect(adminPage.getByTestId("trigger-backfill-error")).toBeVisible();
    await expect(adminPage.getByTestId("trigger-submit")).toBeDisabled();
  });

  test("the dialog refuses an end date without a start date", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    await adminPage.getByTestId("trigger-backfill-to").fill("2023-02-01T00:00");
    await expect(adminPage.getByTestId("trigger-backfill-error")).toBeVisible();
    await expect(adminPage.getByTestId("trigger-submit")).toBeDisabled();
  });

  test("a windowed backfill runs and is audited on the run detail", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    // A wide window that includes the fixture file's date, plus a matching file pattern.
    await adminPage.getByTestId("trigger-backfill-from").fill("2000-01-01T00:00");
    await adminPage.getByTestId("trigger-backfill-to").fill("2100-01-01T00:00");
    await adminPage.getByTestId("trigger-file-pattern").fill("orders.csv");
    await expect(adminPage.getByTestId("trigger-backfill-error")).toHaveCount(0);
    await adminPage.getByTestId("trigger-submit").click();

    // The run detail shows the backfill audit chips and the run reaches success (the file is in the window).
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("run-backfill")).toBeVisible();
    await expect(adminPage.getByTestId("run-backfill")).toContainText("window");
    await expect(adminPage.getByTestId("run-backfill")).toContainText("orders.csv");
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });
  });

  test("a full-load backfill runs and is audited", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    await adminPage.getByTestId("trigger-fullLoad").check();
    await adminPage.getByTestId("trigger-submit").click();

    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("run-backfill")).toContainText("full load");
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });
  });
});
