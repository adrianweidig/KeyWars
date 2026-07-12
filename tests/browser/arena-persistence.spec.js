const { test, expect } = require("@playwright/test");
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

test("Arena trennt Verbindungs-, Vorab- und Persistenzstatus barrierearm", async ({ page }, testInfo) => {
  await login(page, `browser.arena.persistence.${testInfo.workerIndex}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("Persistenzstatus Browser");
  await page.getByLabel("Maximale Teilnehmer").fill("2");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  await expect(page).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
  const roomUrl = page.url();

  let endpointMode = "sequence";
  let statusRequests = 0;
  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));
  await page.route("**/api/arena/*/speicherstatus", (route) => {
    statusRequests += 1;
    const state = endpointMode === "sequence"
      ? statusRequests < 3 ? "Pending" : "Persisted"
      : endpointMode;
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ state })
    });
  });

  await page.goto(`${roomUrl}?arenaTest=pending&case=sequence`);
  const root = page.locator("[data-arena-room]");
  const persistence = page.locator("[data-arena-persistence-status]");
  const connection = page.locator("[data-arena-connection-quality]");
  await expect(root).toHaveAttribute("data-connection-state", "connected");
  await expect(connection).toHaveText("Verbindung: aktiv");
  await expect(connection).toHaveAttribute("role", "status");
  await expect(connection).toHaveAttribute("aria-live", "polite");
  await expect(root).toHaveAttribute("data-persistence-state", "Pending");
  await expect(persistence).toContainText("Ergebnis vorläufig");
  await expect(persistence).not.toContainText("Ergebnis gespeichert");
  await expect(persistence).toHaveAttribute("role", "status");
  await expect(persistence).toHaveAttribute("aria-live", "polite");
  await expect(page.locator("[data-arena-podium] h2")).toHaveText("Podium (vorläufig)");

  await expect(root).toHaveAttribute("data-persistence-state", "Persisted", { timeout: 10_000 });
  await expect(persistence).toHaveText("Ergebnis gespeichert. Rating und XP sind bestätigt.");
  expect(statusRequests).toBe(3);

  endpointMode = "Failed";
  statusRequests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&case=failed`);
  await expect(root).toHaveAttribute("data-persistence-state", "Failed");
  await expect(persistence).toHaveText("Speicherung fehlgeschlagen. Rating und XP wurden nicht vergeben.");
  await expect(persistence).not.toContainText("Ergebnis gespeichert");
  await expect(page.locator("[data-arena-podium] h2")).toHaveText("Podium (Speicherung fehlgeschlagen)");

  endpointMode = "AbortedUnconfirmed";
  statusRequests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&case=aborted`);
  await expect(root).toHaveAttribute("data-persistence-state", "AbortedUnconfirmed");
  await expect(persistence).toContainText("nach Serverabbruch unbestätigt");
  await expect(persistence).not.toContainText("Ergebnis gespeichert");
  await expect(page.locator("[data-arena-podium] h2")).toHaveText("Podium (unbestätigt)");

  endpointMode = "Pending";
  statusRequests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&case=bounded`);
  await expect(root).toHaveAttribute("data-persistence-poll-exhausted", "true", { timeout: 20_000 });
  await expect(persistence).toContainText("Automatische Prüfung beendet");
  expect(statusRequests).toBe(6);
  await page.waitForTimeout(750);
  expect(statusRequests).toBe(6);

  endpointMode = "Persisted";
  statusRequests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&initialPersistence=pending&signalRStart=failed`);
  await expect(root).toHaveAttribute("data-connection-state", "disconnected");
  await expect(root).toHaveAttribute("data-persistence-state", "Persisted");
  await expect(persistence).toHaveText("Ergebnis gespeichert. Rating und XP sind bestätigt.");
  await expect(page.locator("[data-arena-input]")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Bereit" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Starten" })).toBeDisabled();
  await expect(page.locator("[data-arena-leave]")).toBeDisabled();
  expect(statusRequests).toBe(1);

  endpointMode = "AbortedUnconfirmed";
  statusRequests = 0;
  await page.goto(`${roomUrl}?arenaTest=running`);
  const arenaInput = page.locator("[data-arena-input]");
  await expect(root).toHaveAttribute("data-persistence-state", "Running");
  await expect(persistence).toHaveText("Ergebnisstatus: Rennen läuft.");
  await expect(arenaInput).toBeEnabled();
  await page.waitForTimeout(750);
  expect(statusRequests).toBe(0);

  await arenaInput.focus();
  await arenaInput.fill("Front");
  await expect.poll(() => page.evaluate(() => window.__arenaFakeConnection.invocations
    .find((invocation) => invocation.target === "SubmitProgress")?.args[1])).toBe(8);

  await page.evaluate(() => window.__arenaFakeConnection.emitReconnecting());
  await expect(root).toHaveAttribute("data-connection-state", "reconnecting");
  await expect(connection).toHaveText("Verbindung: wird wiederhergestellt");
  await expect(arenaInput).toBeDisabled();

  await page.evaluate(() => window.__arenaFakeConnection.emitReconnected());
  await expect(root).toHaveAttribute("data-connection-state", "connected");
  await expect(connection).toHaveText("Verbindung: aktiv");
  await expect(arenaInput).toBeEnabled();
  await expect(arenaInput).toBeFocused();
  await expect(root).toHaveAttribute("data-persistence-state", "Running");

  const targetText = (await page.locator("[data-arena-target]").textContent()).trim();
  await arenaInput.fill(targetText);
  await expect.poll(() => page.evaluate(() => window.__arenaFakeConnection.invocations
    .find((invocation) => invocation.target === "Finish")?.args[3])).toBe(0);

  await page.evaluate(() => window.__arenaFakeConnection.emitClosed());
  await expect(root).toHaveAttribute("data-connection-state", "disconnected");
  await expect(connection).toHaveText("Verbindung: getrennt");
  await expect(page.locator("[data-arena-leave]")).toBeDisabled();
});
