import { expect, test } from "./helpers";

// The shell: every sidebar item, the theme toggle, and the global search box.

const navTargets: Array<{ nav: string; page: string }> = [
  { nav: "nav-dashboard", page: "page-dashboard" },
  { nav: "nav-chat", page: "page-chat" },
  { nav: "nav-insights", page: "page-insights" },
  { nav: "nav-runs", page: "page-runs" },
  { nav: "nav-nodes", page: "page-nodes" },
  { nav: "nav-repos", page: "page-repos" },
  { nav: "nav-pipelines", page: "page-pipelines" },
  { nav: "nav-schedules", page: "page-schedules" },
  { nav: "nav-datasources", page: "page-datasources" },
  { nav: "nav-key-detection", page: "page-key-detection" },
  { nav: "nav-lineage", page: "page-lineage-graph" },
  { nav: "nav-search", page: "page-search" },
  { nav: "nav-users", page: "page-users" },
];

test.describe("navigation", () => {
  test("every sidebar item opens its page", async ({ adminPage }) => {
    for (const { nav, page: pageId } of navTargets) {
      await adminPage.getByTestId(nav).click();
      await expect(adminPage.getByTestId(pageId)).toBeVisible();
    }
  });

  test("theme toggle flips the brand mode attribute and persists", async ({ adminPage }) => {
    const html = adminPage.locator("html");
    const before = await html.getAttribute("data-sf-theme");
    await adminPage.getByTestId("theme-toggle").click();
    const after = await html.getAttribute("data-sf-theme");
    expect(after).not.toBe(before);
    await adminPage.reload();
    await expect(html).toHaveAttribute("data-sf-theme", after!);
    await adminPage.getByTestId("theme-toggle").click(); // restore
  });

  test("global search routes to the search page with the query applied", async ({ adminPage }) => {
    await adminPage.getByTestId("global-search").fill("orders");
    await adminPage.getByTestId("global-search").press("Enter");
    await expect(adminPage.getByTestId("page-search")).toBeVisible();
    expect(adminPage.url()).toContain("/search?q=orders");
    await expect(adminPage.getByTestId("search-input")).toHaveValue("orders");
  });

  test("unknown routes fall back to the dashboard", async ({ adminPage }) => {
    await adminPage.goto("/definitely-not-a-page");
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible();
  });
});
