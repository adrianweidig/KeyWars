const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function expectConnected(page) {
  await expect(page.locator("[data-arena-connection-quality]"))
    .toHaveText(/Verbindung: (aktiv|neu verbunden)/, { timeout: 30_000 });
}

test("Zwei Tabs desselben Profils teilen einen Arena-Zustand ohne doppelte Person", async ({ page }) => {
  await login(page, `browser.arena.multitab.${Date.now().toString(36)}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("Mehrtab-Arena");
  await page.getByLabel("Maximale Teilnehmer").fill("2");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  await expect(page).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
  const roomUrl = page.url();
  await expectConnected(page);

  const secondTab = await page.context().newPage();
  try {
    await secondTab.goto(roomUrl);
    await expectConnected(secondTab);
    for (const current of [page, secondTab]) {
      await expect(current.locator("[data-arena-participant-count]")).toHaveText("1 Person");
      await expect(current.locator("[data-arena-participants] tr[data-participant-id]")).toHaveCount(1);
    }

    const firstReady = page.locator("[data-arena-ready-form] button");
    const secondReady = secondTab.locator("[data-arena-ready-form] button");
    await expect(firstReady).toBeEnabled();
    await firstReady.click();
    await expect(firstReady).toHaveText("Nicht bereit");
    await expect(secondReady).toHaveText("Nicht bereit");

    await secondTab.close();
    await expectConnected(page);
    await firstReady.click();
    await expect(firstReady).toHaveText("Bereit");
    await expect(page.locator("[data-arena-participant-count]")).toHaveText("1 Person");
  } finally {
    if (!secondTab.isClosed()) {
      await secondTab.close();
    }
  }
});
