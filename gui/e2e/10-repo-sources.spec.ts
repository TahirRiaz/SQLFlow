import { expect, test } from "./helpers";

// Repo source operations beyond the seed: sync-now, and the error surface for a source that cannot sync.

test.describe.serial("repo sources", () => {
  test("sync now re-requests a sync for the healthy source", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("source-sync-now").click();
    // The source stays healthy after the re-sync (no error chip on the row).
    await expect(row.getByTestId("source-error")).toHaveCount(0, { timeout: 60_000 });
  });

  test("a source with an unreachable remote surfaces its sync error", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("open-register-source").click();
    await adminPage.getByTestId("source-name").fill("e2e-broken");
    await adminPage.getByTestId("source-remote-url").fill("C:/definitely/not/a/git/remote");
    await adminPage.getByTestId("register-source-submit").click();

    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-broken" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row.getByTestId("source-error")).toBeVisible({ timeout: 120_000 });
  });
});
