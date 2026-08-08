const { test, expect } = require("@playwright/test");

const TRANSITION_TIMEOUT_MS = 30_000;

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
    .toHaveText(/Verbindung: (aktiv|neu verbunden)/, { timeout: TRANSITION_TIMEOUT_MS });
}

test("Serienraum übergibt die Raumleitung nach Hostverlust ohne Reload", async ({ browser, baseURL }, testInfo) => {
  testInfo.setTimeout(180_000);
  const suffix = Date.now().toString(36);
  const errors = [];
  let hostContext;
  let guestContext;

  try {
    hostContext = await browser.newContext({ baseURL, viewport: { width: 1366, height: 768 } });
    guestContext = await browser.newContext({ baseURL, viewport: { width: 1366, height: 768 } });
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

    await login(host, `arena.series.host.${suffix}`);
    await login(guest, `arena.series.gast.${suffix}`);

    await host.goto("/arena/neu");
    await host.locator('[data-arena-mode-input][value="Series"]').check();
    await host.locator("[data-arena-round-count]").selectOption("3");
    await host.getByLabel("Titel").fill("Serien-Handoff ohne Reload");
    await host.getByLabel("Maximale Teilnehmer").fill("2");
    await host.getByRole("button", { name: "Raum erstellen" }).click();
    await expectConnected(host);

    const roomUrl = host.url();
    const roomCode = ((await host.locator(".room-code strong").textContent()) || "").trim();
    await guest.goto("/arena/beitreten");
    await guest.getByLabel("Raumcode").fill(roomCode);
    await guest.getByRole("button", { name: "Beitreten" }).click();
    await expect(guest).toHaveURL(roomUrl);
    await expectConnected(guest);
    await expect(guest.locator("[data-arena-participant-count]")).toHaveText("2 Personen");

    await host.getByRole("button", { name: "Bereit", exact: true }).click();
    await guest.getByRole("button", { name: "Bereit", exact: true }).click();
    await host.getByRole("button", { name: "Starten", exact: true }).click();
    await expect(guest.locator("[data-arena-state]"))
      .toHaveText("Rennen läuft", { timeout: TRANSITION_TIMEOUT_MS });

    const hostTarget = ((await host.locator("[data-arena-target]").textContent()) || "").trim();
    const guestTarget = ((await guest.locator("[data-arena-target]").textContent()) || "").trim();
    expect(guestTarget).toBe(hostTarget);
    await host.locator("[data-arena-input]").fill(hostTarget);
    await guest.locator("[data-arena-input]").fill(guestTarget);
    await expect(guest.locator("[data-arena-state]"))
      .toHaveText("Rundenergebnis", { timeout: TRANSITION_TIMEOUT_MS });

    const transferStartedAt = Date.now();
    await hostContext.close();
    hostContext = undefined;
    const nextRound = guest.getByRole("button", { name: "Nächste Runde", exact: true });
    await expect(nextRound).toBeVisible({ timeout: TRANSITION_TIMEOUT_MS });
    await expect(nextRound).toBeEnabled({ timeout: TRANSITION_TIMEOUT_MS });
    const transferLatencyMs = Date.now() - transferStartedAt;

    const screenshotPath = testInfo.outputPath("arena-series-host-transfer.png");
    await guest.screenshot({ path: screenshotPath, fullPage: false, animations: "disabled" });
    await testInfo.attach("arena-series-host-transfer", { path: screenshotPath, contentType: "image/png" });

    await nextRound.click();
    await expect(guest.locator("[data-arena-state]"))
      .toHaveText("Rennen läuft", { timeout: TRANSITION_TIMEOUT_MS });
    await expect(guest.locator("[data-arena-round-label]")).toHaveText("Runde 2 von 3");
    expect(guest.url()).toBe(roomUrl);
    expect(errors).toEqual([]);
    console.log(`[arena-series-host-transfer] Handoff=${transferLatencyMs}ms`);
  } finally {
    await Promise.allSettled([hostContext?.close(), guestContext?.close()].filter(Boolean));
  }
});
