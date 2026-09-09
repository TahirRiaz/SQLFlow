import { expect, test, apiLogin, seedSession } from "./helpers";
import { E2E } from "../playwright.config";

// User administration end to end: create, filter, edit, change role, reset password, deactivate/activate, delete,
// and what a non-admin session is allowed to see. Verifications go through real sign-ins, not just UI state.

const runTag = Date.now().toString(36);
const username = `e2e-user-${runTag}`;
const renamed = `e2e-user-renamed-${runTag}`;
const initialPassword = "initial-password-e2e-123";
const resetPassword = "rotated-password-e2e-456";

test.describe.serial("user administration", () => {
  test("create a local user through the dialog", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await expect(adminPage.getByTestId("page-users")).toBeVisible();

    await adminPage.getByTestId("open-create-user").click();
    await adminPage.getByTestId("create-user-username").fill(username);
    await adminPage.getByTestId("create-user-password").fill(initialPassword);
    await adminPage.getByTestId("create-user-displayname").fill("E2E Test User");
    await adminPage.getByTestId("create-user-submit").click();

    await adminPage.getByTestId("filter-username").fill(username);
    const row = adminPage.getByTestId("table-row").filter({ hasText: username });
    await expect(row.first()).toBeVisible({ timeout: 15_000 });
    await expect(row.first().getByTestId("active-badge")).toHaveText("active");

    // The created credential really signs in (viewer scopes only).
    const session = await apiLogin(adminPage.request, username, initialPassword, 15_000);
    expect(session.role).toBe("viewer");
    expect(session.scopes).toEqual(["read"]);
  });

  test("a short password is refused by the create dialog", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("open-create-user").click();
    await adminPage.getByTestId("create-user-username").fill(`e2e-short-${runTag}`);
    await adminPage.getByTestId("create-user-password").fill("too-short");
    // Client validation blocks the submission: the dialog stays open and no such user lands in the list.
    const submit = adminPage.getByTestId("create-user-submit");
    if (await submit.isEnabled()) {
      await submit.click();
    }
    await expect(adminPage.getByTestId("create-user-dialog")).toBeVisible();
    await adminPage.getByTestId("create-user-cancel").click();
    await adminPage.getByTestId("filter-username").fill(`e2e-short-${runTag}`);
    await expect(adminPage.getByTestId("empty-message")).toBeVisible({ timeout: 15_000 });
  });

  test("change the user's role to operator", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("filter-username").fill(username);
    const row = adminPage.getByTestId("table-row").filter({ hasText: username }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-change-role").click();
    await adminPage.getByTestId("user-set-role-select").click();
    await adminPage.getByRole("option", { name: "operator" }).click();
    await adminPage.getByTestId("user-set-role-submit").click();

    await expect(row.getByText("operator").first()).toBeVisible({ timeout: 15_000 });
    const session = await apiLogin(adminPage.request, username, initialPassword, 15_000);
    expect(session.role).toBe("operator");
    expect(session.scopes).toContain("operate");
  });

  test("reset the user's password", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("filter-username").fill(username);
    const row = adminPage.getByTestId("table-row").filter({ hasText: username }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-reset-password").click();
    await adminPage.getByTestId("user-set-password-input").fill(resetPassword);
    await adminPage.getByTestId("user-set-password-submit").click();

    // Old password out, new password in.
    const session = await apiLogin(adminPage.request, username, resetPassword, 15_000);
    expect(session.subject).toBe(username);
    const oldAttempt = await adminPage.request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username, password: initialPassword },
    });
    expect(oldAttempt.status()).toBe(401);
  });

  test("deactivate blocks sign-in; activate restores it", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("filter-username").fill(username);
    const row = adminPage.getByTestId("table-row").filter({ hasText: username }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-deactivate").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(row.getByTestId("active-badge")).toHaveText("inactive", { timeout: 15_000 });

    const refused = await adminPage.request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username, password: resetPassword },
    });
    expect(refused.status()).toBe(401);

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-activate").click();
    await expect(row.getByTestId("active-badge")).toHaveText("active", { timeout: 15_000 });
    const restored = await apiLogin(adminPage.request, username, resetPassword, 15_000);
    expect(restored.subject).toBe(username);
  });

  test("edit the user's display name and sign-in name", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("filter-username").fill(username);
    const row = adminPage.getByTestId("table-row").filter({ hasText: username }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-edit").click();
    await adminPage.getByTestId("edit-user-displayname").fill("E2E Renamed User");
    await adminPage.getByTestId("edit-user-username").fill(renamed);
    await adminPage.getByTestId("edit-user-submit").click();

    await adminPage.getByTestId("filter-username").fill(renamed);
    const renamedRow = adminPage.getByTestId("table-row").filter({ hasText: renamed }).first();
    await expect(renamedRow).toBeVisible({ timeout: 15_000 });
    await expect(renamedRow.getByText("E2E Renamed User")).toBeVisible();

    // The rename moves the credential with it: the new name signs in, the old one is gone.
    const session = await apiLogin(adminPage.request, renamed, resetPassword, 15_000);
    expect(session.subject).toBe(renamed);
    const oldName = await adminPage.request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username, password: resetPassword },
    });
    expect(oldName.status()).toBe(401);
  });

  test("a non-admin session sees no Users nav and cannot open the page", async ({ page, request }) => {
    const session = await apiLogin(request, renamed, resetPassword, 15_000);
    await seedSession(page, session);
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible();
    await expect(page.getByTestId("nav-users")).toHaveCount(0);
    await page.goto("/users");
    // RequireScope bounces a non-admin back to the dashboard.
    await expect(page.getByTestId("page-dashboard")).toBeVisible();
  });

  test("delete removes the account for good", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-users").click();
    await adminPage.getByTestId("filter-username").fill(renamed);
    const row = adminPage.getByTestId("table-row").filter({ hasText: renamed }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("user-actions").click();
    await adminPage.getByTestId("user-menu-delete").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();

    await expect(adminPage.getByTestId("empty-message")).toBeVisible({ timeout: 15_000 });
    const refused = await adminPage.request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username: renamed, password: resetPassword },
    });
    expect(refused.status()).toBe(401);
  });
});
