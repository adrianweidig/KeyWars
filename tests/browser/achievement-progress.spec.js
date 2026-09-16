const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function completeValidAttempt(page) {
  await page.evaluate(async () => {
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
    await postJson("/api/spielen/abschliessen", {
      attemptId: session.id,
      nonce: session.nonce,
      input: session.text,
      backspaces: 0,
      focusLosses: 0,
      clientDurationMilliseconds: 5_100,
      wordDurationsMilliseconds: []
    });
  });
}

function achievementCard(page, title) {
  return page.locator(".achievement-card", {
    has: page.getByRole("heading", { name: title, exact: true })
  });
}

test("Gesperrte Erfolge zeigen zugänglichen quantitativen Fortschritt", async ({ page }, testInfo) => {
  testInfo.setTimeout(90_000);
  await login(page, `browser.achievement.progress.${Date.now().toString(36)}`);
  await page.goto("/profil/erfolge");

  const firstAttempt = achievementCard(page, "Erster gültiger Versuch");
  const firstProgress = firstAttempt.getByRole("progressbar", {
    name: "Fortschritt für Erster gültiger Versuch"
  });
  await expect(firstAttempt).toHaveClass(/is-locked/);
  await expect(firstProgress).toHaveAttribute("value", "0");
  await expect(firstProgress).toHaveAttribute("max", "1");
  await expect(firstAttempt).toContainText("0 von 1 Runde");

  const rating = achievementCard(page, "Rating 1050");
  await expect(rating.getByRole("progressbar", { name: "Fortschritt für Rating 1050" }))
    .toHaveAttribute("value", "1000");
  await expect(rating).toContainText("1000 von 1050 Rating-Punkte");

  await completeValidAttempt(page);
  await page.goto("/profil/erfolge");

  await expect(firstAttempt).toHaveClass(/is-unlocked/);
  await expect(firstAttempt).toContainText("Freigeschaltet am");
  await expect(firstAttempt.getByRole("progressbar")).toHaveCount(0);

  const fiveAttempts = achievementCard(page, "Fünf Runden");
  await expect(fiveAttempts).toHaveClass(/is-locked/);
  await expect(fiveAttempts.getByRole("progressbar", { name: "Fortschritt für Fünf Runden" }))
    .toHaveAttribute("value", "1");
  await expect(fiveAttempts).toContainText("1 von 5 Runden");
});
