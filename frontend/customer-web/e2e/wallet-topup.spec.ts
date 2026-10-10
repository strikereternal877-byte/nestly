import { test, expect } from "@playwright/test";
import { loadFixture, authenticateAsSeededCustomer } from "./setup/auth";

/**
 * Adding money to the wallet (sandbox gateway). The server ships this switched
 * off (`WalletTopUp:Enabled`), so this suite expects the stack to have been
 * started with `WalletTopUp__Enabled=true` - see e2e/README.md.
 *
 * What earns the file: the credit lands once, the screen says so, the balance
 * and the ledger agree with it, and the booking summary offers the same
 * detour with a way back.
 */

test.describe("Wallet: add money", () => {
  test("adding money through the sandbox gateway credits the wallet and shows in the ledger", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    await page.goto("/wallet");
    await expect(page.getByRole("heading", { name: "Wallet", level: 1 })).toBeVisible();

    const panel = page.locator("#add-money");
    await expect(panel.getByRole("heading", { name: "Add money" })).toBeVisible({ timeout: 15_000 });
    await expect(panel).toContainText("can only be used on Glavyx services");

    // Quick amounts fill the field; the button says what it will add.
    await panel.getByRole("button", { name: "₹500.00", exact: true }).click();
    await expect(panel.getByLabel("Amount (₹)")).toHaveValue("500");
    await expect(panel.getByRole("button", { name: "Add ₹500.00" })).toBeEnabled();

    // An amount below the minimum is refused before anything is sent.
    await panel.getByLabel("Amount (₹)").fill("5");
    await expect(panel.getByRole("button", { name: "Add money" })).toBeDisabled();
    await expect(panel).toContainText("Enter an amount between");

    await panel.getByLabel("Amount (₹)").fill("500");
    await panel.getByRole("button", { name: "Add ₹500.00" }).click();

    // The sandbox has no payment page: the customer completes it here.
    const complete = panel.getByRole("button", { name: /Complete ₹500\.00 \(Sandbox\)/ });
    await expect(complete).toBeVisible({ timeout: 15_000 });
    await complete.click();

    await expect(panel.getByText("₹500.00 added to your wallet")).toBeVisible({ timeout: 15_000 });

    // The ledger carries it as a credit from the gateway, not as some other kind of credit.
    // (The ledger renders as a card list on a phone and a table on a desktop; only one is visible at a time.)
    await expect(page.locator("table").getByText("Money added").first()).toBeVisible({ timeout: 15_000 });
  });

  test("the booking summary offers adding money, and the wallet remembers where the customer came from", async ({
    page,
  }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    await page.goto(`/booking/summary?serviceSlug=${fixture.serviceSlug}`);
    await expect(page.getByRole("heading", { name: "Review your booking" })).toBeVisible();

    // The wallet card only appears once the booking can be priced (an address and a slot are chosen).
    await expect(page.locator('input[name="address"]').first()).toBeChecked({ timeout: 15_000 });
    const slotButton = page.getByRole("button", { name: /E2E Anytime/ });
    await expect(slotButton).toBeVisible({ timeout: 15_000 });
    await slotButton.click();

    const addMoney = page.getByRole("link", { name: "Add money to wallet" });
    await expect(addMoney).toBeVisible({ timeout: 15_000 });
    await expect(addMoney).toHaveAttribute("href", /^\/wallet\?addMoney=1&returnTo=%2Fbooking%2Fsummary/);

    await addMoney.click();
    await expect(page).toHaveURL(/\/wallet\?addMoney=1/);
    await expect(page.locator("#add-money")).toBeVisible({ timeout: 15_000 });
  });

  test("a top-up that came from a booking summary offers the way back", async ({ page }) => {
    const fixture = loadFixture();
    await authenticateAsSeededCustomer(page, fixture);

    // What the PayU return page sees: a remembered destination and a finished top-up.
    await page.goto("/wallet");
    const panel = page.locator("#add-money");
    await expect(panel).toBeVisible({ timeout: 15_000 });

    const created = await page.evaluate(async (apiBase) => {
      const token = sessionStorage.getItem("nestly.accessToken");
      const res = await fetch(`${apiBase}/api/v1/wallet/top-ups`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ amount: 200 }),
      });
      return res.json();
    }, process.env.CONSUMER_API_URL ?? "http://localhost:5257");
    expect(created.topUpId).toBeTruthy();

    await page.evaluate(
      ({ apiBase, id }) => {
        const token = sessionStorage.getItem("nestly.accessToken");
        sessionStorage.setItem("nestly.wallet-topup.return-to", "/booking/summary?serviceSlug=demo");
        return fetch(`${apiBase}/api/v1/wallet/top-ups/${id}/simulate`, {
          method: "POST",
          headers: { Authorization: `Bearer ${token}` },
        });
      },
      { apiBase: process.env.CONSUMER_API_URL ?? "http://localhost:5257", id: created.topUpId },
    );

    await page.goto(`/wallet/topup/${created.topUpId}/return`);
    await expect(page.getByText("₹200.00 added to your wallet")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole("link", { name: "Back to your booking" })).toHaveAttribute(
      "href",
      "/booking/summary?serviceSlug=demo",
    );
    await expect(page.getByRole("link", { name: "View wallet" })).toBeVisible();
  });
});
