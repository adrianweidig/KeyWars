const { test, expect } = require("@playwright/test");

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
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function completeWordsAttempt(page) {
  return page.evaluate(async () => {
    const postJson = async (url, body) => {
      const response = await fetch(url, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        throw new Error(`${url} antwortete mit ${response.status}.`);
      }

      return response.json();
    };

    const session = await postJson("/api/spielen/start", {
      mode: "Words10",
      trainingTextId: null,
      sprintSeconds: 0,
      wordCount: 10
    });
    await postJson("/api/spielen/begin", {
      attemptId: session.id,
      nonce: session.nonce
    });
    await new Promise((resolve) => setTimeout(resolve, 5_100));
    const finish = {
      attemptId: session.id,
      nonce: session.nonce,
      input: session.text,
      backspaces: 0,
      focusLosses: 0,
      clientDurationMilliseconds: 1000,
      wordDurationsMilliseconds: []
    };
    const completion = await postJson("/api/spielen/abschliessen", finish);
    return { finish, completion };
  });
}

test("Saisonboard vergibt sichtbare Punkte genau einmal und bleibt filterstabil", async ({ page }, testInfo) => {
  testInfo.setTimeout(120_000);
  const username = `browser.season.${Date.now().toString(36)}`;
  await login(page, username);
  const { finish, completion } = await completeWordsAttempt(page);
  expect(completion.wpm).toBeGreaterThan(0);

  await page.goto("/ranglisten?board=season&period=all&scope=organization");
  const seasonTab = page.getByRole("navigation", { name: "Wettbewerbsbereiche" })
    .getByRole("link", { name: "Saison", exact: true });
  await expect(seasonTab).toHaveAttribute("aria-current", "page");
  await expect(page.locator(".competition-controls select[name='period']")).toHaveCount(0);
  await expect(page.locator(".competition-controls input[name='period']")).toHaveValue("all");
  await expect(page.locator(".competition-main h2")).toContainText("Saison");
  await expect(page.locator(".competition-board-head p")).toContainText("frühere Saisons bleiben erhalten");
  await expect(page.getByRole("link", { name: "Saisonpunkte sammeln" })).toHaveAttribute("href", "/arena");

  const ownRow = page.locator(".competition-table tbody tr.is-current");
  await expect(ownRow).toContainText(displayName(username));
  const points = ((await ownRow.locator("td[data-label='Punkte']").textContent()) || "").trim();
  expect(Number(points.replace(/\D/g, ""))).toBeGreaterThan(0);
  await expect(page.locator(".competition-rules")).toContainText("genau einmal");

  await page.evaluate(async (request) => {
    const response = await fetch("/api/spielen/abschliessen", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(request)
    });
    if (!response.ok) {
      throw new Error(`Idempotenter Abschluss antwortete mit ${response.status}.`);
    }
  }, finish);
  await page.reload();
  await expect(page.locator(".competition-table tbody tr.is-current td[data-label='Punkte']"))
    .toHaveText(points);
});
