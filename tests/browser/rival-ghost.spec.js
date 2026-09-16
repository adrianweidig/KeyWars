const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function completeTextAttempt(page, textId) {
  return page.evaluate(async (trainingTextId) => {
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
      mode: "Text",
      trainingTextId,
      sprintSeconds: 0,
      wordCount: 80
    });
    await postJson("/api/spielen/begin", {
      attemptId: session.id,
      nonce: session.nonce
    });
    const minimumSeconds = Math.ceil(Array.from(session.text).length / 30);
    await new Promise((resolve) => setTimeout(resolve, (minimumSeconds * 1000) + 150));
    await postJson("/api/spielen/abschliessen", {
      attemptId: session.id,
      nonce: session.nonce,
      input: session.text,
      backspaces: 0,
      focusLosses: 0,
      clientDurationMilliseconds: 1000,
      wordDurationsMilliseconds: []
    });
    return session.id;
  }, textId);
}

async function firstStandardTextId(page) {
  await page.goto("/texte");
  const href = await page.locator(".text-library-grid .text-card")
    .filter({ hasText: "Standard" })
    .first()
    .getByRole("link", { name: "Trainieren" })
    .getAttribute("href");
  const match = href?.match(/[0-9a-f-]{36}/i);
  if (!match) {
    throw new Error("Kein Standardtext für den Rivalen-Browsertest gefunden.");
  }

  return match[0];
}

test("Rivalen-Geist ist auffindbar, opt-in-sicher und startet erst nach der Auswahl", async ({ page, browser, baseURL }, testInfo) => {
  testInfo.setTimeout(180_000);
  const marker = `browser-rival-${testInfo.workerIndex}-${Date.now()}`;

  await login(page, `${marker}-owner`);
  const textId = await firstStandardTextId(page);
  const ownerAttemptId = await completeTextAttempt(page, textId);

  await page.goto("/profil?zeitraum=7&seite=1");
  const ghostLink = page.getByRole("link", { name: "Geisterrennen" }).first();
  await expect(ghostLink).toBeVisible();
  await ghostLink.click();
  await expect(page).toHaveURL(new RegExp(`/spielen/geist/${ownerAttemptId}$`, "i"));
  const typingApp = page.locator("[data-typing-app]");
  await expect(typingApp).toHaveAttribute("data-mode", "Ghost");
  await expect(page.locator("[data-rival-ghost-reference]"))
    .toContainText("Eigener Geist");
  await expect(page.locator("[data-rival-ghost-selection]"))
    .toContainText("Eingaben und Tastenfolgen bleiben privat");
  await expect(typingApp.locator("[data-input]")).toBeDisabled();
  await expect(typingApp.locator("[data-start]")).toBeVisible();

  const rivalContext = await browser.newContext({ baseURL });
  const rivalPage = await rivalContext.newPage();
  try {
    await login(rivalPage, `${marker}-rival`);
    await rivalPage.goto("/profil/einstellungen");
    await rivalPage.getByLabel("Bestleistungen als Rivalen-Geist freigeben").check();
    await rivalPage.getByRole("button", { name: "Speichern" }).click();
    await expect(rivalPage.getByRole("status")).toContainText("Einstellungen gespeichert");
    await completeTextAttempt(rivalPage, textId);

    await page.reload();
    const select = page.locator("[data-rival-ghost-select]");
    await expect(select).toHaveCount(1);
    const rivalOption = select.locator("option").nth(1);
    const rivalProfileId = await rivalOption.getAttribute("value");
    expect(rivalProfileId).toMatch(/^[0-9a-f-]{36}$/i);
    await select.selectOption(rivalProfileId);
    await page.getByRole("button", { name: "Auswählen" }).click();
    await expect(page).toHaveURL(new RegExp(`rivalProfileId=${rivalProfileId}`, "i"));
    await expect(typingApp).toHaveAttribute("data-mode", "RivalGhost");
    await expect(page.locator("[data-rival-ghost-reference]"))
      .toContainText(/rival/i);
    await expect(typingApp.locator("[data-input]")).toBeDisabled();

    await typingApp.locator("[data-start]").click();
    await expect(typingApp.locator("[data-input]")).toBeEnabled();

    await rivalPage.goto("/profil/einstellungen");
    await rivalPage.getByLabel("Bestleistungen als Rivalen-Geist freigeben").uncheck();
    await rivalPage.getByRole("button", { name: "Speichern" }).click();
    await page.reload();
    await expect(page.locator("[data-rival-ghost-select]")).toHaveCount(0);
    await expect(page.locator("[data-rival-ghost-reference]"))
      .toContainText("Eigener Geist");
    await expect(typingApp).toHaveAttribute("data-mode", "Ghost");
  } finally {
    await rivalContext.close();
  }
});
