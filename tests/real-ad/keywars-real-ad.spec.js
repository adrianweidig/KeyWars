const { test, expect } = require("@playwright/test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const requiredEnv = [
  "KEYWARS_REAL_AD_HOST_USER",
  "KEYWARS_REAL_AD_HOST_PASSWORD",
  "KEYWARS_REAL_AD_GUEST_USER",
  "KEYWARS_REAL_AD_GUEST_PASSWORD"
];
const disabledEnv = [
  "KEYWARS_REAL_AD_DISABLED_USER",
  "KEYWARS_REAL_AD_DISABLED_PASSWORD"
];
const hasRequiredEnv = requiredEnv.every((name) => Boolean(process.env[name]));
const hasDisabledEnv = disabledEnv.every((name) => Boolean(process.env[name]));
const evidence = {
  generatedAt: new Date().toISOString(),
  commit: process.env.KEYWARS_REAL_AD_COMMIT || process.env.GITHUB_SHA || "unknown",
  host: os.hostname(),
  target: process.env.KEYWARS_REAL_AD_BASE_URL || "http://127.0.0.1:18080",
  checks: []
};
const forwardedHeaders = {
  "X-Forwarded-For": process.env.KEYWARS_REAL_AD_FORWARDED_FOR || "127.0.0.1",
  "X-Forwarded-Proto": process.env.KEYWARS_REAL_AD_FORWARDED_PROTO || "https"
};

test.describe("KeyWars Real-AD-Abnahme", () => {
  test.skip(!hasRequiredEnv, `Real-AD-Abnahme benötigt ${requiredEnv.join(", ")}.`);
  test.describe.configure({ mode: "serial" });

  test.afterAll(() => {
    if (!hasRequiredEnv) {
      return;
    }

    const reportPath = process.env.KEYWARS_REAL_AD_REPORT ||
      path.join("output", "playwright", "real-ad", "keywars-real-ad-report.json");
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
  });

  test("ungültiges AD-Passwort wird abgelehnt", async ({ page }) => {
    await page.goto("/anmelden");
    await page.getByLabel("Benutzername").fill(process.env.KEYWARS_REAL_AD_HOST_USER);
    await page.getByLabel("Passwort").fill(`${process.env.KEYWARS_REAL_AD_HOST_PASSWORD}-ungueltig`);
    await page.getByRole("button", { name: "Anmelden" }).click();
    await expect(page.getByText("Anmeldung fehlgeschlagen. Bitte prüfe Benutzername und Passwort.")).toBeVisible();
    evidence.checks.push({ name: "negative-login", status: "passed" });
  });

  test("deaktivierter AD-Nutzer wird abgelehnt", async ({ page }) => {
    test.skip(!hasDisabledEnv, `Disabled-AD-Abnahme benötigt ${disabledEnv.join(", ")}.`);

    await page.goto("/anmelden");
    await page.getByLabel("Benutzername").fill(process.env.KEYWARS_REAL_AD_DISABLED_USER);
    await page.getByLabel("Passwort").fill(process.env.KEYWARS_REAL_AD_DISABLED_PASSWORD);
    await page.getByRole("button", { name: "Anmelden" }).click();
    await expect(page.getByText("Anmeldung fehlgeschlagen. Bitte prüfe Benutzername und Passwort.")).toBeVisible();
    evidence.checks.push({ name: "disabled-login", status: "passed" });
  });

  test("zwei reale AD-Nutzer absolvieren eine Arena-Runde", async ({ browser, baseURL }, testInfo) => {
    const hostContext = await browser.newContext({ baseURL, extraHTTPHeaders: forwardedHeaders, colorScheme: "dark", reducedMotion: "reduce" });
    const guestContext = await browser.newContext({ baseURL, extraHTTPHeaders: forwardedHeaders, colorScheme: "dark", reducedMotion: "reduce" });
    const suffix = `${Date.now()}-${testInfo.workerIndex}`;

    try {
      const host = await hostContext.newPage();
      const guest = await guestContext.newPage();

      const hostDisplayName = await login(host, process.env.KEYWARS_REAL_AD_HOST_USER, process.env.KEYWARS_REAL_AD_HOST_PASSWORD);
      const guestDisplayName = await login(guest, process.env.KEYWARS_REAL_AD_GUEST_USER, process.env.KEYWARS_REAL_AD_GUEST_PASSWORD);
      expect(hostDisplayName).not.toEqual(guestDisplayName);

      await host.goto("/arena/neu");
      await host.getByLabel("Titel").fill(`Real-AD Smoke ${suffix}`);
      await host.getByLabel("Maximale Teilnehmer").fill("2");
      await host.getByRole("button", { name: "Raum erstellen" }).click();
      await expect(host).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);

      const roomUrl = host.url();
      const roomCode = (await host.locator(".room-code strong").textContent()).trim();

      await guest.goto("/arena/beitreten");
      await guest.getByLabel("Raumcode").fill(roomCode);
      await guest.getByRole("button", { name: "Beitreten" }).click();
      await expect(guest).toHaveURL(roomUrl);
      await expect(host.locator("[data-arena-participants] tr", { hasText: guestDisplayName })).toHaveCount(1);

      const duplicateGuest = await guestContext.newPage();
      await duplicateGuest.goto(roomUrl);
      await expect(duplicateGuest.locator("[data-arena-participants] tr", { hasText: guestDisplayName })).toHaveCount(1);
      await expect(host.locator("[data-arena-participants] tr", { hasText: guestDisplayName })).toHaveCount(1);
      await duplicateGuest.close();

      await guest.reload();
      await expect(guest.locator("[data-arena-participants] tr", { hasText: hostDisplayName })).toHaveCount(1);

      await guest.getByRole("button", { name: "Bereit" }).click();
      await expect(guest.getByRole("button", { name: "Nicht bereit" })).toBeVisible();
      await host.getByRole("button", { name: "Bereit" }).click();
      await expect(host.getByRole("button", { name: "Nicht bereit" })).toBeVisible();

      await host.getByRole("button", { name: "Starten" }).click();
      await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 15_000 });
      await expect(guest.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 15_000 });

      const hostTarget = (await host.locator("[data-arena-target]").textContent()).trim();
      const guestTarget = (await guest.locator("[data-arena-target]").textContent()).trim();
      await host.locator("[data-arena-input]").fill(hostTarget.slice(0, Math.max(1, Math.floor(hostTarget.length / 3))));
      await guest.locator("[data-arena-input]").fill(guestTarget);
      await host.locator("[data-arena-input]").fill(hostTarget);

      await expect(host.locator("[data-arena-podium]")).toBeVisible({ timeout: 15_000 });
      await expect(guest.locator("[data-arena-podium]")).toBeVisible({ timeout: 15_000 });
      await expect(host.locator("[data-arena-podium]")).toContainText(hostDisplayName);
      await expect(host.locator("[data-arena-podium]")).toContainText(guestDisplayName);

      evidence.checks.push({
        name: "two-real-ad-users-arena",
        status: "passed",
        roomCode,
        hostDisplayName,
        guestDisplayName
      });
    } finally {
      await guestContext.close();
      await hostContext.close();
    }
  });
});

async function login(page, username, password) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill(password);
  await page.getByRole("button", { name: "Anmelden" }).click();
  const heading = page.getByRole("heading", { name: /^Hallo, / });
  await expect(heading).toBeVisible();
  const text = await heading.textContent();
  return text.replace(/^Hallo,\s*/, "").trim();
}
