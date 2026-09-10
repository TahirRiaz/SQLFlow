import { expect, test } from "./helpers";

// The insights page: the optimization dashboard over the estate seeded and run by the earlier specs. The
// KPI row and the attention/time/flows cards always render (empty states included); the flows table has
// rows whenever the earlier specs' runs fall inside the selected window.

test.describe("insights", () => {
  test("the page renders its KPI row, cards, and window toggle", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-insights").click();
    await expect(adminPage.getByTestId("page-insights")).toBeVisible();

    for (const kpi of ["kpi-processing-time", "kpi-failure-rate", "kpi-rows-loaded", "kpi-attention", "kpi-flows-measured"]) {
      await expect(adminPage.getByTestId(kpi)).toBeVisible();
    }

    await expect(adminPage.getByTestId("insights-attention-card")).toBeVisible();
    await expect(adminPage.getByTestId("insights-time-chart-card")).toBeVisible();
    await expect(adminPage.getByTestId("insights-flows-card")).toBeVisible();
    await expect(adminPage.getByTestId("warehouse-health-card")).toBeVisible();

    // The window toggle re-queries; the page stays rendered (no error surface) on every choice.
    await adminPage.getByTestId("insights-window-toggle").getByText("30d").click();
    await expect(adminPage.getByTestId("kpi-processing-time")).toBeVisible();
    await adminPage.getByTestId("insights-window-toggle").getByText("7d").click();
    await expect(adminPage.getByTestId("kpi-processing-time")).toBeVisible();
  });

  test("a measured flow drills down to its step breakdown", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-insights").click();
    await expect(adminPage.getByTestId("page-insights")).toBeVisible();

    // Earlier specs ran the seeded pipeline, so the flows table has at least one row inside the 30d window.
    await adminPage.getByTestId("insights-window-toggle").getByText("30d").click();
    const table = adminPage.getByTestId("insights-flows-table");
    await expect(table).toBeVisible();
    const firstRow = table.locator("tbody tr").first();
    await expect(firstRow).toBeVisible();
    await firstRow.click();

    // The steps sheet opens; with or without recorded step timings it shows its surface (table or empty state).
    await expect(adminPage.getByTestId("insights-steps-sheet")).toBeVisible();
    await adminPage.keyboard.press("Escape");
    await expect(adminPage.getByTestId("insights-steps-sheet")).not.toBeVisible();
  });
});
