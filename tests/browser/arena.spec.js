const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hallo, ${displayName(username)}`) })).toBeVisible();
}

function displayName(username) {
  return username
    .replace(/[._]/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1).toLowerCase()}`)
    .join(" ");
}

test("Dashboard und Einstellungen rendern im echten Browser", async ({ page }) => {
  await login(page, "browser.smoke");

  await expect(page.getByText("Tagesfokus")).toBeVisible();
  await expect(page.getByText("30-Tage-Übersicht")).toBeVisible();
  await page.goto("/profil/einstellungen");
  await expect(page.getByRole("heading", { name: "Einstellungen" })).toBeVisible();
  await expect(page.getByText("Identität aus AD/LDAP")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "de");
});

test("Arena laeuft mit zwei getrennten Browserkontexten ueber SignalR", async ({ browser, baseURL }, testInfo) => {
  const hostContext = await browser.newContext({ baseURL, colorScheme: "dark", reducedMotion: "reduce" });
  const guestContext = await browser.newContext({ baseURL, colorScheme: "dark", reducedMotion: "reduce" });
  const suffix = testInfo.workerIndex.toString();
  const hostName = `arena.host.${suffix}`;
  const guestName = `arena.gast.${suffix}`;

  try {
    const host = await hostContext.newPage();
    const guest = await guestContext.newPage();

    await login(host, hostName);
    await login(guest, guestName);

    await host.goto("/arena/neu");
    await host.getByLabel("Titel").fill(`Browser-Arena ${suffix}`);
    await host.getByLabel("Maximale Teilnehmer").fill("2");
    await host.getByRole("button", { name: "Raum erstellen" }).click();
    await expect(host).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
    await expect(host.getByRole("button", { name: "Starten" })).toBeVisible();

    const roomUrl = host.url();
    const roomCode = (await host.locator(".room-code strong").textContent()).trim();
    await expect(host.getByRole("button", { name: "Code kopieren" })).toBeVisible();
    await expect(host.getByRole("button", { name: "Einladung teilen" })).toBeVisible();

    await guest.goto("/arena/beitreten");
    await guest.getByLabel("Raumcode").fill(roomCode);
    await guest.getByRole("button", { name: "Beitreten" }).click();
    await expect(guest).toHaveURL(roomUrl);
    await expect(guest.getByRole("button", { name: "Starten" })).toHaveCount(0);

    await guest.getByRole("button", { name: "Bereit" }).click();
    await expect(guest.getByRole("button", { name: "Nicht bereit" })).toBeVisible();
    await host.getByRole("button", { name: "Bereit" }).click();
    await expect(host.getByRole("button", { name: "Nicht bereit" })).toBeVisible();

    await host.getByRole("button", { name: "Starten" }).click();
    await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });
    await expect(guest.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });

    const hostTarget = (await host.locator("[data-arena-target]").textContent()).trim();
    const guestTarget = (await guest.locator("[data-arena-target]").textContent()).trim();
    await expect(host.locator("[data-arena-input]")).toBeEnabled();
    await expect(guest.locator("[data-arena-input]")).toBeEnabled();
    await host.locator("[data-arena-input]").fill(hostTarget);
    await guest.locator("[data-arena-input]").fill(guestTarget);

    await expect(host.locator("[data-arena-podium]")).toBeVisible({ timeout: 12_000 });
    await expect(guest.locator("[data-arena-podium]")).toBeVisible({ timeout: 12_000 });
    await expect(host.locator("[data-arena-podium]")).toContainText(displayName(hostName));
    await expect(guest.locator("[data-arena-podium]")).toContainText(displayName(guestName));
  } finally {
    await guestContext.close();
    await hostContext.close();
  }
});
