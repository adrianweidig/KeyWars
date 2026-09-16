const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function createText(page, title, visibility) {
  await page.goto("/texte/neu");
  await page.getByLabel("Titel").fill(title);
  await page.locator("select[name='Input.Visibility']").selectOption(visibility);
  await page.locator("textarea[name='Input.Body']")
    .fill(`Trainingstext ${title}: klare Wörter, Umlaute und genügend Inhalt für eine verlässliche Sammlung.`);
  await page.getByRole("button", { name: "Text speichern" }).click();
  await expect(page).toHaveURL(/\/texte\/[0-9a-f-]{36}$/i);
}

function collectionCard(page, name) {
  return page.locator("article.card").filter({
    has: page.getByRole("heading", { name, exact: true })
  });
}

async function expectNoSeriousAccessibilityViolations(page) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  expect(result.violations.filter(({ impact }) => impact === "critical" || impact === "serious"))
    .toEqual([]);
}

test("Textsammlungen lassen sich vollständig und sichtbarkeitskonform verwalten", async ({ page, browser, baseURL }, testInfo) => {
  testInfo.setTimeout(180_000);
  const marker = `browser-collection-${testInfo.workerIndex}-${Date.now()}`;
  const firstTitle = `${marker} Privattext`;
  const secondTitle = `${marker} Organisationstext`;
  const originalName = `${marker} Entwurf`;
  const updatedName = `${marker} Freigabe`;

  await login(page, `${marker}-owner`);
  await createText(page, firstTitle, "Private");
  await createText(page, secondTitle, "Organization");

  await page.goto("/texte/sammlungen/neu");
  await page.locator("input[name='Input.Name']").fill(originalName);
  await page.locator("input[name='Input.Description']").fill("Private Ausgangssammlung");
  await page.locator("select[name='Input.Visibility']").selectOption("Private");
  await page.getByRole("checkbox", { name: firstTitle, exact: true }).check();
  await page.getByRole("button", { name: "Sammlung speichern" }).click();

  await expect(page).toHaveURL(/\/texte\/sammlungen\/[0-9a-f-]{36}$/i);
  const collectionPath = new URL(page.url()).pathname;
  await expect(page.getByRole("heading", { name: originalName, exact: true })).toBeVisible();
  await expect(page.locator(".collection-text-card")).toHaveCount(1);
  await expect(page.locator(".collection-text-card")).toContainText(firstTitle);
  await expectNoSeriousAccessibilityViolations(page);

  await page.getByRole("link", { name: "Bearbeiten", exact: true }).click();
  await expect(page.getByRole("checkbox", { name: firstTitle, exact: true })).toBeChecked();
  await page.locator("input[name='Input.Name']").fill(updatedName);
  await page.locator("input[name='Input.Description']").fill("Freigegebene Sammlung mit aktualisiertem Inhalt");
  await page.locator("select[name='Input.Visibility']").selectOption("Organization");
  await page.getByRole("checkbox", { name: firstTitle, exact: true }).uncheck();
  await page.getByRole("checkbox", { name: secondTitle, exact: true }).check();
  await page.getByRole("button", { name: "Änderungen speichern" }).click();

  await expect(page).toHaveURL(collectionPath);
  await expect(page.getByRole("heading", { name: updatedName, exact: true })).toBeVisible();
  await expect(page.locator(".page-header .lead")).toHaveText("Freigegebene Sammlung mit aktualisiertem Inhalt");
  await expect(page.locator(".collection-text-card")).toHaveCount(1);
  await expect(page.locator(".collection-text-card")).toContainText(secondTitle);
  await expect(page.locator(".collection-text-card")).not.toContainText(firstTitle);

  const viewerContext = await browser.newContext({ baseURL });
  const viewer = await viewerContext.newPage();
  try {
    await login(viewer, `${marker}-viewer`);
    await viewer.goto("/texte/sammlungen");
    const visibleCollection = collectionCard(viewer, updatedName);
    await expect(visibleCollection).toHaveCount(1);
    await visibleCollection.getByRole("link", { name: "Öffnen" }).click();
    await expect(viewer).toHaveURL(collectionPath);
    await expect(viewer.locator(".collection-text-card")).toContainText(secondTitle);
    await expect(viewer.getByRole("link", { name: "Bearbeiten", exact: true })).toHaveCount(0);
    await expect(viewer.getByRole("button", { name: "Sammlung löschen" })).toHaveCount(0);
    await expectNoSeriousAccessibilityViolations(viewer);

    await page.goto(collectionPath);
    await page.getByRole("button", { name: "Sammlung löschen" }).click();
    await expect(page).toHaveURL(/\/texte\/sammlungen$/);
    await expect(collectionCard(page, updatedName)).toHaveCount(0);
    await viewer.goto("/texte/sammlungen");
    await expect(collectionCard(viewer, updatedName)).toHaveCount(0);
  } finally {
    await viewerContext.close();
  }
});
