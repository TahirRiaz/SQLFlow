import { expect, test } from "@playwright/test";
import { E2E } from "../playwright.config";
import { adminSession, seedSession } from "./helpers";

// Sign-in surface: the route guard, every login method the control plane offers, error rendering, and sign-out.

test.describe("authentication", () => {
  test("unauthenticated visit is redirected to the login page with a return target", async ({ page }) => {
    await page.goto("/runs");
    await expect(page.getByTestId("login-card")).toBeVisible();
    expect(page.url()).toContain("/login?returnTo=%2Fruns");
  });

  test("wrong credentials show an error and stay on the login page", async ({ page, request }) => {
    await adminSession(request); // ensures bootstrap provisioning has completed before probing logins
    await page.goto("/login");
    await page.getByTestId("login-username").fill("no-such-user");
    await page.getByTestId("login-password").fill("definitely-wrong-password");
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("login-error")).toBeVisible();
    await expect(page.getByTestId("login-card")).toBeVisible();
  });

  test("local sign-in lands on the dashboard and returns to the requested page", async ({ page, request }) => {
    await adminSession(request);
    await page.goto("/nodes");
    await expect(page.getByTestId("login-card")).toBeVisible();
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-password").fill(E2E.adminPassword);
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("page-nodes")).toBeVisible();
  });

  test("\"keep me signed in\" persists the session to localStorage instead of sessionStorage", async ({ page, request }) => {
    await adminSession(request);
    await page.goto("/login");
    await expect(page.getByTestId("login-card")).toBeVisible();
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-password").fill(E2E.adminPassword);
    await page.getByTestId("login-remember").check();
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("page-dashboard")).toBeVisible();

    const stored = await page.evaluate(() => ({
      local: window.localStorage.getItem("sqlflow.session"),
      session: window.sessionStorage.getItem("sqlflow.session"),
    }));
    expect(stored.local).not.toBeNull();
    expect(stored.session).toBeNull();
  });

  test("local sign-in without \"keep me signed in\" stays in sessionStorage only", async ({ page, request }) => {
    await adminSession(request);
    await page.goto("/login");
    await expect(page.getByTestId("login-card")).toBeVisible();
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-password").fill(E2E.adminPassword);
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("page-dashboard")).toBeVisible();

    const stored = await page.evaluate(() => ({
      local: window.localStorage.getItem("sqlflow.session"),
      session: window.sessionStorage.getItem("sqlflow.session"),
    }));
    expect(stored.local).toBeNull();
    expect(stored.session).not.toBeNull();
  });

  test("a returning user's username and \"keep me signed in\" choice are prefilled after sign-out", async ({ page, request }) => {
    await adminSession(request);
    await page.goto("/login");
    await page.getByTestId("login-username").fill(E2E.adminUsername);
    await page.getByTestId("login-password").fill(E2E.adminPassword);
    await page.getByTestId("login-remember").check();
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("page-dashboard")).toBeVisible();

    await page.getByTestId("account-menu-button").click();
    await page.getByTestId("account-logout").click();
    await expect(page.getByTestId("login-card")).toBeVisible();

    // Signing out is exactly when the prefill has to survive: the session is gone, the name is not.
    await expect(page.getByTestId("login-username")).toHaveValue(E2E.adminUsername);
    await expect(page.getByTestId("login-remember")).toBeChecked();
    // The credential itself never comes back, and the caret sits where the typing still has to happen.
    await expect(page.getByTestId("login-password")).toHaveValue("");
    await expect(page.getByTestId("login-password")).toBeFocused();
  });

  test("a failed sign-in does not become the prefill", async ({ page, request }) => {
    await adminSession(request);
    await page.goto("/login");
    await page.getByTestId("login-username").fill("no-such-user");
    await page.getByTestId("login-password").fill("definitely-wrong-password");
    await page.getByTestId("login-submit").click();
    await expect(page.getByTestId("login-error")).toBeVisible();

    await page.goto("/login");
    await expect(page.getByTestId("login-username")).toHaveValue("");
  });

  test("account menu shows the subject and role; sign out returns to login", async ({ page, request }) => {
    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await page.getByTestId("account-menu-button").click();
    await expect(page.getByTestId("account-subject")).toContainText(E2E.adminUsername);
    await expect(page.getByTestId("account-subject")).toContainText("admin");
    await page.getByTestId("account-logout").click();
    await expect(page.getByTestId("login-card")).toBeVisible();
    // The session really ended: the persisted session is gone. (A goto here would prove nothing: the test's
    // init script would immediately re-seed sessionStorage on the navigation.)
    const persisted = await page.evaluate(() => window.sessionStorage.getItem("sqlflow.session"));
    expect(persisted).toBeNull();
  });

  test("bootstrap secret sign-in works from the advanced expander", async ({ page }) => {
    await page.goto("/login");
    await page.getByTestId("login-bootstrap-expander").click();
    await page.getByTestId("login-bootstrap-secret").fill(E2E.bootstrapSecret);
    await page.getByTestId("login-bootstrap-submit").click();
    await expect(page.getByTestId("page-dashboard")).toBeVisible();
  });

  test("entra button is hidden when the control plane has SSO disabled", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByTestId("login-card")).toBeVisible();
    await expect(page.getByTestId("login-bootstrap-expander")).toBeVisible();
    await expect(page.getByTestId("login-entra")).toHaveCount(0);
  });
});
