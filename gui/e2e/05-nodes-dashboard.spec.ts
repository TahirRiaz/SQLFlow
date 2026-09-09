import { expect, test } from "./helpers";

// The fleet page (the control plane's in-process worker heartbeats as a node) and the dashboard's live numbers.

test.describe("nodes and dashboard", () => {
  test("the in-process worker appears as an online node", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-nodes").click();
    await expect(adminPage.getByTestId("page-nodes")).toBeVisible();
    const onlineBadge = adminPage.getByTestId("online-badge").filter({ hasText: "online" });
    await expect(onlineBadge.first()).toBeVisible({ timeout: 60_000 });
  });

  test("dashboard KPIs reflect the seeded estate and link through", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-dashboard").click();
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible();

    // The seed spec synced one repo with one pipeline and earlier specs ran runs; the cards reflect that.
    await expect(adminPage.getByText("Runs by state")).toBeVisible();

    // Every KPI card renders and the cards navigate to their pages (spot-check two of the links).
    for (const kpi of ["kpi-repos", "kpi-pipelines", "kpi-nodes", "kpi-schedules", "kpi-repo-sources", "kpi-runs-24h"]) {
      await expect(adminPage.getByTestId(kpi)).toBeVisible();
    }
    await expect(adminPage.getByTestId("dashboard-as-of")).toBeVisible();

    await adminPage.getByTestId("kpi-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();
    await adminPage.getByTestId("nav-dashboard").click();
    await adminPage.getByTestId("kpi-nodes").click();
    await expect(adminPage.getByTestId("page-nodes")).toBeVisible();
  });
});
