const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("synthetisches-testpasswort");
  await page.getByRole("button", { name: "Anmelden", exact: true }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function expectInitialControlVisible(page, locator) {
  const bounds = await locator.boundingBox();
  const bottom = await page.evaluate(() => {
    const navigation = document.querySelector(".mobile-bottom-nav")?.getBoundingClientRect();
    return navigation?.height ? navigation.top : window.innerHeight;
  });
  expect(bounds).not.toBeNull();
  expect(bounds.y).toBeGreaterThanOrEqual(54);
  expect(bounds.y + bounds.height).toBeLessThanOrEqual(bottom);
}

for (const viewport of [
  { width: 1366, height: 768 },
  { width: 768, height: 900 },
  { width: 390, height: 844 },
  { width: 320, height: 844 }
]) {
  for (const colorScheme of ["dark", "light"]) {
    test(`Training und Textverwaltung bleiben erreichbar: ${viewport.width}px ${colorScheme}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await page.emulateMedia({ colorScheme });
      await login(page, `browser.ui.${viewport.width}.${colorScheme}`);
      for (const route of ["/spielen/sprint", "/spielen/woerter"]) {
        await page.goto(route);
        await expect(page.locator("[data-input]")).toBeEnabled();
        await expectInitialControlVisible(page, page.locator("[data-input]"));
      }

      await page.goto("/texte");
      await expectInitialControlVisible(page, page.locator(".text-library-header")
        .getByRole("link", { name: "Neuer Text", exact: true }));
      await expectInitialControlVisible(page, page.locator(".text-library-header")
        .getByRole("link", { name: "Sammlungen", exact: true }));

      await page.getByLabel("Suche", { exact: true }).fill("kein-treffer-ui-20260930");
      await page.getByRole("button", { name: "Filtern", exact: true }).click();
      const empty = page.locator(".text-library-grid .empty-state");
      await expect(empty).toContainText("Keine Texte gefunden");
      const emptyWidth = (await empty.boundingBox()).width;
      const gridWidth = (await page.locator(".text-library-grid").boundingBox()).width;
      expect(emptyWidth).toBeGreaterThanOrEqual(gridWidth - 2);

      await page.goto("/texte/sammlungen/neu");
      const options = page.getByRole("group", { name: "Verfügbare Trainingstexte", exact: true });
      expect((await options.boundingBox()).height).toBeLessThanOrEqual(320);
      const lastText = options.getByRole("checkbox").last();
      await lastText.focus();
      await page.keyboard.press("Space");
      await expect(lastText).toBeChecked();
      await expect(lastText).toBeInViewport();
      await expect(page.getByRole("button", { name: "Sammlung speichern" })).toBeVisible();

      const result = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      expect(result.violations.filter((violation) => ["critical", "serious"].includes(violation.impact))
        .map((violation) => ({ id: violation.id, targets: violation.nodes.map((node) => node.target) }))).toEqual([]);

      for (const route of ["/herausforderungen", "/profil/export"]) {
        await page.goto(route);
        const clipped = await page.locator(".page-header, .section-title-row").evaluateAll((rows) => rows
          .flatMap((row) => [...row.children])
          .filter((element) => element.getBoundingClientRect().right > window.innerWidth + 1)
          .map((element) => element.textContent.trim()));
        expect(clipped).toEqual([]);
      }
    });
  }
}

test("Text- und Sammlungsfehler sind sichtbar, beschrieben und per Tastatur korrigierbar", async ({ page }) => {
  await login(page, "browser.ui.validation");
  for (const [route, submit, errors] of [
    ["/texte/neu", "Text speichern", ["Der Titel ist erforderlich.", "Der Text ist erforderlich."]],
    ["/texte/sammlungen/neu", "Sammlung speichern", ["Der Name ist erforderlich."]]
  ]) {
    await page.goto(route);
    await page.locator("form.form-grid").first().evaluate((form) => { form.noValidate = true; });
    await page.getByRole("button", { name: submit, exact: true }).click();
    const summary = page.locator(".validation-summary-errors");
    for (const message of errors) await expect(summary).toContainText(message);
    await expect(summary).toBeFocused();
    const firstField = page.locator(".input-validation-error").first();
    await expect(firstField).toHaveAttribute("aria-invalid", "true");
    await expect(firstField).toHaveAttribute("aria-describedby", await summary.getAttribute("id"));
    await page.keyboard.press("Tab");
    await expect(firstField).toBeFocused();
  }
});

test("Eine private Herausforderung lässt sich vom eingeladenen Nutzer vollständig spielen", async ({ page, browser, baseURL }) => {
  const guestContext = await browser.newContext({ baseURL });
  const guest = await guestContext.newPage();
  try {
    await login(guest, "browser.ui.private.gast");
    await login(page, "browser.ui.private.host");
    await page.goto("/texte/neu");
    await page.locator("[name='Input.Title']").fill("Privater UI-Challenge-Text");
    await page.locator("[name='Input.Body']").fill("Gemeinsam üben wir einen privaten Text. Nur die eingeladenen Personen nehmen an dieser Herausforderung teil. Der Text bleibt in der Bibliothek privat und die Runde zeigt ein Ergebnis für jeden Teilnehmer.");
    await page.getByRole("button", { name: "Text speichern" }).click();
    const textPath = new URL(page.url()).pathname;
    await page.getByRole("link", { name: "Herausfordern", exact: true }).click();
    await page.getByLabel("Titel", { exact: true }).fill("Private UI-Challenge");
    await page.locator("[data-person-query]").fill("browser.ui.private.gast");
    await page.getByRole("option", { name: /^Browser Ui Private Gast(?: ·|$)/ }).click();
    await page.getByRole("button", { name: "Herausforderung senden" }).click();
    const challengePath = new URL(page.url()).pathname;
    await guest.goto("/texte?Suche=Privater%20UI-Challenge-Text");
    await expect(guest.locator(`a[href='${textPath}']`)).toHaveCount(0);
    const textId = textPath.split("/").at(-1);
    for (const id of [textId, "00000000-0000-0000-0000-000000000000"]) {
      for (const path of [`/texte/${id}`, `/texte/${id}/kopieren`, `/spielen/text/${id}`]) {
        expect((await guestContext.request.get(path)).status()).toBe(404);
        if (id === textId) expect((await page.context().request.get(path)).status()).toBe(200);
      }
    }
    await guest.goto(challengePath);
    await guest.getByRole("button", { name: "Annehmen", exact: true }).click();
    await guest.getByRole("link", { name: "Runde 1 spielen" }).click();
    await expect(guest.locator("[data-input]")).toBeEnabled();
    await guest.locator("[data-input]").pressSequentially(await guest.locator("[data-target]").innerText(), { delay: 40 });
    await expect(guest.locator(".finish-panel")).toBeVisible();
    await guest.getByRole("link", { name: "Zum Serienstand", exact: true }).click();
    await expect(guest.locator("tbody tr").filter({ hasText: "Browser Ui Private Gast" }))
      .toContainText("Fertig");
  } finally {
    await guestContext.close();
  }
});
