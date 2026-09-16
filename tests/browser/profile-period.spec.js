const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function createCompletedAttempts(page, count) {
  await page.evaluate(async (attemptCount) => {
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

    for (let index = 0; index < attemptCount; index += 1) {
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
      await postJson("/api/spielen/abschliessen", {
        attemptId: session.id,
        nonce: session.nonce,
        input: session.text,
        backspaces: 0,
        focusLosses: 0,
        clientDurationMilliseconds: 1000,
        wordDurationsMilliseconds: []
      });
    }
  }, count);
}

test("Profilzeitraum ist zugänglich, validiert und URL-stabil", async ({ page }) => {
  await login(page, "browser.profile.period");
  await page.goto("/profil?zeitraum=7&seite=2147483647");

  const selector = page.getByRole("navigation", { name: "Profilzeitraum auswählen" });
  await expect(selector).toBeVisible();
  await expect(selector.locator("[data-profile-period]")).toHaveCount(3);
  await expect(selector.locator('[data-profile-period="7"]')).toHaveAttribute("aria-current", "page");
  await expect(page.locator("[data-profile-period-empty]")).toContainText("letzten 7 Tagen");

  const thirtyDayLink = selector.locator('[data-profile-period="30"]');
  const href = await thirtyDayLink.getAttribute("href");
  const target = new URL(href, page.url());
  expect(target.pathname).toBe("/profil");
  expect(target.searchParams.get("zeitraum")).toBe("30");
  expect(target.searchParams.get("seite")).toBe("1");

  await thirtyDayLink.click();
  await expect(page).toHaveURL(/\/profil\?zeitraum=30&seite=1$/);
  await expect(selector.locator('[data-profile-period="30"]')).toHaveAttribute("aria-current", "page");

  await page.goto("/profil?zeitraum=365&seite=-2");
  await expect(selector.locator('[data-profile-period="90"]')).toHaveAttribute("aria-current", "page");
  await expect(page.locator("[data-profile-period-empty]")).toContainText("letzten 90 Tagen");
});

test("Historienpaging behält den gewählten Zeitraum", async ({ page }) => {
  await login(page, "browser.profile.paging");
  await createCompletedAttempts(page, 11);
  await page.goto("/profil?zeitraum=7&seite=1");

  await expect(page.locator("section", { has: page.getByRole("heading", { name: "Historie · 7 Tage" }) }).locator("tbody tr")).toHaveCount(10);
  const next = page.getByRole("navigation", { name: "Historie Seiten" }).getByRole("link", { name: "Weiter" });
  const nextTarget = new URL(await next.getAttribute("href"), page.url());
  expect(nextTarget.searchParams.get("zeitraum")).toBe("7");
  expect(nextTarget.searchParams.get("seite")).toBe("2");

  await next.click();
  await expect(page).toHaveURL(/\/profil\?zeitraum=7&seite=2$/);
  await expect(page.locator("section", { has: page.getByRole("heading", { name: "Historie · 7 Tage" }) }).locator("tbody tr")).toHaveCount(1);
  const previous = page.getByRole("navigation", { name: "Historie Seiten" }).getByRole("link", { name: "Zurück" });
  const previousTarget = new URL(await previous.getAttribute("href"), page.url());
  expect(previousTarget.searchParams.get("zeitraum")).toBe("7");
  expect(previousTarget.searchParams.get("seite")).toBe("1");
});
