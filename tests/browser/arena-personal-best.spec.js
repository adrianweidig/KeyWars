const { test, expect } = require("@playwright/test");
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

test("Arena zeigt persönliche Bestwerte ausschließlich nach bestätigter Speicherung", async ({ page }, testInfo) => {
  await login(page, `browser.arena.personal-best.${testInfo.workerIndex}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("Persönlicher Bestwert");
  await page.getByLabel("Maximale Teilnehmer").fill("2");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  const roomUrl = page.url();

  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));

  let mode = "new";
  let requests = 0;
  const personalBest = {
    currentWpm: 58.4,
    previousBestWpm: 52.1,
    bestWpm: 58.4,
    isNewBest: true,
    scopeLabel: "Klassisches Rennen · gleicher Zieltext"
  };
  await page.route("**/api/arena/*/speicherstatus", (route) => {
    requests += 1;
    if (mode === "new" && requests < 4) {
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ state: "Pending", personalBest })
      });
    }

    const body = mode === "failed"
      ? { state: "Failed", personalBest }
      : mode === "existing"
        ? {
            state: "Persisted",
            personalBest: {
              currentWpm: 48.2,
              previousBestWpm: 58.4,
              bestWpm: 58.4,
              isNewBest: false,
              scopeLabel: personalBest.scopeLabel
            }
          }
        : { state: "Persisted", personalBest };
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(body)
    });
  });

  await page.goto(`${roomUrl}?arenaTest=pending&best=new`);
  const root = page.locator("[data-arena-room]");
  const panel = page.locator("[data-arena-personal-best]");
  await expect.poll(() => requests).toBeGreaterThanOrEqual(1);
  await expect(root).toHaveAttribute("data-persistence-state", "Pending");
  await expect(panel).toBeHidden();

  await expect(root).toHaveAttribute("data-persistence-state", "Persisted", { timeout: 10_000 });
  await expect(root).toHaveAttribute("data-personal-best-state", "new");
  await expect(panel).toBeVisible();
  await expect(panel.locator("[data-arena-personal-best-value]")).toHaveText("Bestwert: 58,4 WPM");
  await expect(panel.locator("[data-arena-personal-best-context]")).toContainText("Dieses Rennen: 58,4 WPM · Bisher: 52,1 WPM");
  await expect(panel.locator("[data-arena-personal-best-context]")).toContainText("Klassisches Rennen · gleicher Zieltext");
  await expect(panel.getByText("Neuer Bestwert", { exact: true })).toBeVisible();

  mode = "existing";
  requests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&best=existing`);
  await expect(root).toHaveAttribute("data-personal-best-state", "confirmed", { timeout: 10_000 });
  await expect(panel.locator("[data-arena-personal-best-value]")).toHaveText("Bestwert: 58,4 WPM");
  await expect(panel.locator("[data-arena-personal-best-context]")).toContainText("Dieses Rennen: 48,2 WPM · Bisher: 58,4 WPM");
  await expect(panel.getByText("Neuer Bestwert", { exact: true })).toBeHidden();

  mode = "failed";
  requests = 0;
  await page.goto(`${roomUrl}?arenaTest=pending&best=failed`);
  await expect(root).toHaveAttribute("data-persistence-state", "Failed", { timeout: 10_000 });
  await expect(root).toHaveAttribute("data-personal-best-state", "hidden");
  await expect(panel).toBeHidden();
  await expect(page.getByText("Neuer Bestwert", { exact: true })).toBeHidden();
});
