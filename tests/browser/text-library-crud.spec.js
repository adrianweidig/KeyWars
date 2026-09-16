const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

function textCard(page, title) {
  return page.locator(".text-library-grid article.text-card").filter({
    has: page.getByRole("heading", { name: title, exact: true })
  });
}

async function expectNoSeriousAccessibilityViolations(page) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  expect(result.violations.filter(({ impact }) => impact === "critical" || impact === "serious")).toEqual([]);
}

test("Eigene Trainingstexte lassen sich vollständig und sichtbarkeitskonform verwalten", async ({ page, browser, baseURL }, testInfo) => {
  testInfo.setTimeout(180_000);
  const marker = `browser-text-crud-${testInfo.workerIndex}-${Date.now()}`;
  const createdTitle = `${marker} Entwurf`;
  const updatedTitle = `${marker} Freigabe`;
  const copyTitle = `${updatedTitle} Kopie`;
  const updatedBody = "Überarbeiteter Übungstext mit Umlauten, klarer Aussage und stabiler Browserprüfung.";

  await login(page, `${marker}-owner`);
  await page.goto("/texte");
  await page.getByRole("link", { name: "Neuer Text", exact: true }).first().click();
  await page.getByLabel("Titel").fill(createdTitle);
  await page.getByLabel("Sichtbarkeit").first().selectOption("Private");
  await page.locator("textarea[name='Input.Body']").fill("Erster Entwurf für den vollständigen Browser-CRUD-Test.");
  await page.getByRole("button", { name: "Text speichern" }).click();

  await expect(page).toHaveURL(/\/texte\/[0-9a-f-]{36}$/i);
  const originalPath = new URL(page.url()).pathname;
  await expect(page.getByRole("heading", { name: createdTitle, exact: true })).toBeVisible();
  await expect(page.locator(".page-header .lead")).toContainText("Privat");
  await expect(page.getByRole("link", { name: "Bearbeiten", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Löschen", exact: true })).toBeVisible();
  await expectNoSeriousAccessibilityViolations(page);

  await page.getByRole("link", { name: "Bearbeiten", exact: true }).click();
  await page.getByLabel("Titel").fill(updatedTitle);
  await page.getByLabel("Sichtbarkeit").selectOption("Organization");
  await page.locator("textarea[name='Input.Body']").fill(updatedBody);
  await page.getByRole("button", { name: "Änderungen speichern" }).click();

  await expect(page).toHaveURL(originalPath);
  await expect(page.getByRole("heading", { name: updatedTitle, exact: true })).toBeVisible();
  await expect(page.locator(".page-header .lead")).toContainText("Organisation");
  await expect(page.locator("pre.preformatted")).toHaveText(updatedBody);

  await page.getByRole("button", { name: "Kopie erstellen" }).click();
  await expect(page).toHaveURL(/\/texte\/[0-9a-f-]{36}$/i);
  const copyPath = new URL(page.url()).pathname;
  expect(copyPath).not.toBe(originalPath);
  await expect(page.getByRole("heading", { name: copyTitle, exact: true })).toBeVisible();
  await expect(page.locator(".page-header .lead")).toContainText("Privat");
  await expect(page.locator("pre.preformatted")).toHaveText(updatedBody);
  await expect(page.getByRole("link", { name: "Bearbeiten", exact: true })).toBeVisible();

  const viewerContext = await browser.newContext({ baseURL });
  const viewerPage = await viewerContext.newPage();
  try {
    await login(viewerPage, `${marker}-viewer`);
    await viewerPage.goto("/texte");
    await viewerPage.getByLabel("Suche").fill(marker);
    await viewerPage.getByRole("button", { name: "Filtern" }).click();
    await expect(textCard(viewerPage, updatedTitle)).toHaveCount(1);
    await expect(textCard(viewerPage, copyTitle)).toHaveCount(0);

    await textCard(viewerPage, updatedTitle).getByRole("link", { name: "Ansehen" }).click();
    await expect(viewerPage.getByRole("heading", { name: updatedTitle, exact: true })).toBeVisible();
    await expect(viewerPage.getByRole("link", { name: "Bearbeiten", exact: true })).toHaveCount(0);
    await expect(viewerPage.getByRole("button", { name: "Löschen", exact: true })).toHaveCount(0);
    await expectNoSeriousAccessibilityViolations(viewerPage);

    await page.goto(copyPath);
    await page.getByRole("button", { name: "Löschen", exact: true }).click();
    await expect(page).toHaveURL(/\/texte$/);
    await page.goto(originalPath);
    await page.getByRole("button", { name: "Löschen", exact: true }).click();
    await expect(page).toHaveURL(/\/texte$/);

    await page.getByLabel("Suche").fill(marker);
    await page.getByRole("button", { name: "Filtern" }).click();
    await expect(textCard(page, updatedTitle)).toHaveCount(0);
    await expect(textCard(page, copyTitle)).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Keine Texte gefunden" })).toBeVisible();

    await viewerPage.goto(`/texte?Suche=${encodeURIComponent(marker)}`);
    await expect(textCard(viewerPage, updatedTitle)).toHaveCount(0);
  } finally {
    await viewerContext.close();
  }
});
