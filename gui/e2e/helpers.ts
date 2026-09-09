import { expect, test as base, type APIRequestContext, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";

export interface SessionSeed {
  token: string;
  subject: string;
  role: string | null;
  scopes: string[];
  expiresAtMs: number;
}

interface LoginBody {
  accessToken: string;
  expiresIn: number;
  subject: string;
  role: string;
  scopes: string[];
}

/**
 * Signs in through the API, retrying while bootstrap provisioning (which creates the e2e admin on a fresh
 * database) completes in the control plane's background. Fails loudly after the deadline.
 */
export async function apiLogin(
  request: APIRequestContext, username: string, password: string, deadlineMs = 120_000,
): Promise<SessionSeed> {
  const deadline = Date.now() + deadlineMs;
  let lastStatus = 0;
  while (Date.now() < deadline) {
    const response = await request.post(`${E2E.apiBaseUrl}/api/v1/auth/login`, {
      data: { username, password },
    });
    lastStatus = response.status();
    if (response.ok()) {
      const body = (await response.json()) as LoginBody;
      return {
        token: body.accessToken,
        subject: body.subject,
        role: body.role,
        scopes: body.scopes,
        expiresAtMs: Date.now() + body.expiresIn * 1000,
      };
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  throw new Error(
    `Could not sign in as ${username} within ${deadlineMs}ms (last status ${lastStatus}); did bootstrap provisioning run?`,
  );
}

let cachedAdmin: SessionSeed | null = null;

export async function adminSession(request: APIRequestContext): Promise<SessionSeed> {
  if (!cachedAdmin || cachedAdmin.expiresAtMs < Date.now() + 60_000) {
    cachedAdmin = await apiLogin(request, E2E.adminUsername, E2E.adminPassword);
  }

  return cachedAdmin;
}

/** Seeds the SPA's sessionStorage before any page script runs, exactly as AuthContext persists it. */
export async function seedSession(page: Page, session: SessionSeed): Promise<void> {
  await page.addInitScript((value) => {
    window.sessionStorage.setItem("sqlflow.session", JSON.stringify(value));
  }, session);
}

/** A page already signed in as the bootstrap-provisioned admin, parked on the dashboard. */
export const test = base.extend<{ adminPage: Page }>({
  adminPage: async ({ page, request }, use) => {
    const session = await adminSession(request);
    await seedSession(page, session);
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible();
    await use(page);
  },
});

export { expect } from "@playwright/test";
