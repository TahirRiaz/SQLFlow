import { expect, test } from "./helpers";

// The dedicated unique-key detection page: scope pickers, options, run gating, and history. Everything here is
// client-side or catalog-backed; no live SQL Server datasource is assumed, so detection itself is never run.

test.describe("key detection", () => {
  test("renders the target pickers, options, and history without a scope selected", async ({ adminPage }) => {
    await adminPage.goto("/key-detection");
    await expect(adminPage.getByTestId("page-key-detection")).toBeVisible();

    await expect(adminPage.getByTestId("kd-datasource")).toBeVisible();
    await expect(adminPage.getByTestId("kd-schema")).toBeDisabled();
    await expect(adminPage.getByTestId("kd-object")).toBeDisabled();

    // Nothing selected: the run button must be gated off, and the history list renders (empty or not).
    await expect(adminPage.getByTestId("kd-run")).toBeDisabled();
    await expect(adminPage.getByTestId("kd-history")).toBeVisible();
  });

  test("options are adjustable and validate their bounds", async ({ adminPage }) => {
    await adminPage.goto("/key-detection");

    // The custom sample size field appears only for the Sample mode.
    await expect(adminPage.getByTestId("kd-sample-size")).toHaveCount(0);
    await adminPage.getByTestId("kd-sample-mode").getByLabel("Sample", { exact: true }).check();
    await expect(adminPage.getByTestId("kd-sample-size")).toBeVisible();

    // An out-of-bounds key width shows its helper text; restoring a valid value clears it.
    await adminPage.getByTestId("kd-max-columns").fill("0");
    await expect(adminPage.getByText("1 to 16 columns.")).toBeVisible();
    await adminPage.getByTestId("kd-max-columns").fill("4");
    await expect(adminPage.getByText("1 to 16 columns.")).toHaveCount(0);

    await adminPage.getByTestId("kd-trust-declared").click();
    await adminPage.getByTestId("kd-verify").click();
  });

  test("a deep link prefills the scope from the URL", async ({ adminPage }) => {
    await adminPage.goto("/key-detection?ref=%24%7Benv%3AE2E_SOURCE%7D&kind=MSSQL&schema=dbo&object=Orders");
    await expect(adminPage.getByTestId("page-key-detection")).toBeVisible();

    await expect(adminPage.getByTestId("kd-datasource")).toHaveValue("${env:E2E_SOURCE}");
    await expect(adminPage.getByTestId("kd-schema")).toHaveValue("dbo");
    await expect(adminPage.getByTestId("kd-object")).toHaveValue("dbo.Orders");
  });
});
