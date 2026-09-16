const { test, expect } = require("@playwright/test");

const TIMEOUT_MS = 30_000;

function displayName(username) {
  return username
    .replace(/[._]/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1).toLowerCase()}`)
    .join(" ");
}

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".sidebar-profile")).toContainText(displayName(username));
}

async function expectConnected(page) {
  await expect(page.locator("[data-arena-connection-quality]"))
    .toHaveText(/Verbindung: (aktiv|neu verbunden)/, { timeout: TIMEOUT_MS });
}

test("Arena-Revanche bleibt hostgebunden, idempotent und übernimmt Gruppe sowie Teamkonfiguration", async ({ browser, baseURL }, testInfo) => {
  testInfo.setTimeout(180_000);
  const suffix = Date.now().toString(36);
  const hostName = `arena.rematch.host.${suffix}`;
  const guestName = `arena.rematch.gast.${suffix}`;
  const errors = [];
  const hostContext = await browser.newContext({ baseURL, viewport: { width: 1366, height: 768 } });
  const guestContext = await browser.newContext({ baseURL, viewport: { width: 1366, height: 768 } });

  try {
    const host = await hostContext.newPage();
    const guest = await guestContext.newPage();
    for (const [page, role] of [[host, "host"], [guest, "guest"]]) {
      page.on("pageerror", (error) => errors.push(`${role}: ${error.message}`));
      page.on("console", (message) => {
        if (message.type() === "error") {
          errors.push(`${role}: ${message.text()}`);
        }
      });
    }

    await login(host, hostName);
    await login(guest, guestName);
    await host.goto("/arena/neu");
    await host.locator('[data-arena-mode-input][value="Team"]').check();
    await host.getByLabel("Titel").fill("Team-Revanche");
    await host.getByLabel("Maximale Teilnehmer").fill("2");
    await host.getByRole("button", { name: "Raum erstellen" }).click();
    await expectConnected(host);

    const sourceUrl = host.url();
    const sourcePath = new URL(sourceUrl).pathname;
    const sourceCode = ((await host.locator(".room-code strong").textContent()) || "").trim();
    await expect(host.locator("[data-arena-rematch-actions]")).toBeHidden();
    await guest.goto("/arena/beitreten");
    await guest.getByLabel("Raumcode").fill(sourceCode);
    await guest.getByRole("button", { name: "Beitreten" }).click();
    await expect(guest).toHaveURL(sourceUrl);
    await expectConnected(guest);

    await host.getByRole("button", { name: "Bereit", exact: true }).click();
    await guest.getByRole("button", { name: "Bereit", exact: true }).click();
    await host.getByRole("button", { name: "Starten", exact: true }).click();
    await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: TIMEOUT_MS });
    await expect(guest.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: TIMEOUT_MS });
    const sourceTarget = ((await host.locator("[data-arena-target]").textContent()) || "").trim();
    await host.locator("[data-arena-input]").fill(sourceTarget);
    await guest.locator("[data-arena-input]").fill(sourceTarget);
    await expect(host.locator("[data-arena-podium]")).toBeVisible({ timeout: TIMEOUT_MS });
    await expect(guest.locator("[data-arena-podium]")).toBeVisible({ timeout: TIMEOUT_MS });
    await expect(host.locator("[data-arena-rematch-actions]")).toBeVisible();
    await expect(guest.locator("[data-arena-rematch-actions]")).toBeHidden();

    await guest.locator("[data-arena-rematch-form]").evaluate((form) => {
      form.querySelector("button").disabled = false;
      form.requestSubmit();
    });
    await expect(guest).toHaveURL((url) =>
      url.pathname === sourcePath && url.searchParams.get("handler") === "Rematch");
    await expect(guest.locator("[role='alert']")).toContainText("Nur die Raumleitung", { timeout: TIMEOUT_MS });
    await expect(guest.locator("[data-arena-podium]")).toBeVisible();

    await host.getByRole("button", { name: "Revanche mit gleicher Gruppe" }).click();
    await expect(host).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
    const rematchUrl = host.url();
    expect(rematchUrl).not.toBe(sourceUrl);
    const rematchCode = ((await host.locator(".room-code strong").textContent()) || "").trim();
    expect(rematchCode).not.toBe(sourceCode);
    await expect(host.getByRole("heading", { name: "Team-Revanche – Revanche" })).toBeVisible();
    await expect(host.locator("[data-arena-round-label]")).toHaveText("Runde 1 von 1");
    await expect(host.locator("[data-arena-room]")).toHaveAttribute("data-max-participants", "2");

    await host.goto(sourceUrl);
    await expect(host).toHaveURL(sourceUrl);
    await expect(host.locator("[data-arena-rematch-actions]")).toBeVisible({ timeout: TIMEOUT_MS });
    await host.getByRole("button", { name: "Revanche mit gleicher Gruppe" }).click();
    await expect(host).toHaveURL(rematchUrl);

    await guest.goto(rematchUrl);
    await expectConnected(guest);
    await expect(guest.locator("[data-arena-participant-count]")).toHaveText("2 Personen");
    const teams = await guest.locator("[data-arena-participants] tr").evaluateAll((rows) =>
      rows.map((row) => ({
        name: row.querySelector("td")?.textContent?.trim(),
        team: row.querySelectorAll("td")[1]?.textContent?.trim()
      })).sort((left, right) => left.name.localeCompare(right.name)));
    expect(teams).toEqual([
      { name: displayName(guestName), team: "Bravo" },
      { name: displayName(hostName), team: "Alpha" }
    ].sort((left, right) => left.name.localeCompare(right.name)));

    await host.getByRole("button", { name: "Bereit", exact: true }).click();
    await guest.getByRole("button", { name: "Bereit", exact: true }).click();
    await host.getByRole("button", { name: "Starten", exact: true }).click();
    await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: TIMEOUT_MS });
    await expect(host.locator("[data-arena-target]")).toHaveText(sourceTarget);
    await expect(guest.locator("[data-arena-target]")).toHaveText(sourceTarget);
    expect(errors).toEqual([]);
  } finally {
    await Promise.allSettled([hostContext.close(), guestContext.close()]);
  }
});
