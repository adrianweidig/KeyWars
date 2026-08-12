const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function expectNoSeriousViolations(page, label) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  const violations = result.violations
    .filter((violation) => violation.impact === "critical" || violation.impact === "serious")
    .map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      targets: violation.nodes.map((node) => node.target)
    }));

  expect(violations, `${label}: kritische oder schwere Accessibility-Befunde`).toEqual([]);
}

test("Login und App-Shell sind tastatur- und Axe-tauglich", async ({ page }, testInfo) => {
  testInfo.setTimeout(120_000);
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.goto("/anmelden");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await expectNoSeriousViolations(page, "Login Light");

  await login(page, "browser.accessibility.shell");
  await page.keyboard.press("Tab");
  await expect(page.locator(".skip-link")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.locator("#hauptinhalt")).toBeFocused();
  await expect(page.locator("#desktop-sidebar .sidebar-nav a[aria-current='page']")).toHaveCount(1);
  await expect(page.locator("#desktop-sidebar .sidebar-nav a[aria-current='page']")).toContainText("Start");
  await expectNoSeriousViolations(page, "Dashboard Light");

  const sidebarToggle = page.locator("[data-sidebar-toggle]");
  await sidebarToggle.click();
  await expect(sidebarToggle).toHaveAccessibleName("Sidebar ausklappen");
  await expectNoSeriousViolations(page, "Dashboard mit eingeklappter Sidebar");
  await sidebarToggle.click();

  await page.setViewportSize({ width: 390, height: 844 });
  const opener = page.locator("[data-mobile-menu-opener]");
  await opener.click();
  await expect(opener).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("[data-mobile-menu]")).toHaveAttribute("aria-hidden", "false");
  await expect(page.locator("[data-mobile-menu] button")).toBeFocused();
  await expect(page.locator("[data-mobile-menu] .sidebar-nav a[aria-current='page']")).toHaveCount(1);
  await expect(page.locator(".mobile-bottom-nav a[aria-current='page']")).toHaveCount(1);
  await page.keyboard.press("Escape");
  await expect(opener).toBeFocused();
  await expect(opener).toHaveAttribute("aria-expanded", "false");
  await expectNoSeriousViolations(page, "Dashboard Mobile");

  await page.goto("/profil/einstellungen");
  await expect(page.locator("#desktop-sidebar .sidebar-nav a[aria-current='page']")).toContainText("Einstellungen");
  await expect(page.locator("[data-mobile-menu] .sidebar-nav a[aria-current='page']")).toContainText("Einstellungen");
  await expect(page.locator(".mobile-bottom-nav a[aria-current='page']")).toContainText("Profil");
});

test("Formfehler werden beschrieben und fokussieren das fehlerhafte Feld", async ({ page }) => {
  await page.goto("/anmelden");
  await page.locator("form").evaluate((form) => {
    form.noValidate = true;
  });
  await page.getByRole("button", { name: "Anmelden" }).click();

  const username = page.getByLabel("Benutzername");
  const usernameError = page.locator("#login-username-error");
  await expect(usernameError).toContainText("erforderlich");
  await expect(usernameError).toHaveAttribute("role", "alert");
  await expect(username).toHaveAttribute("aria-invalid", "true");
  await expect(username).toHaveAttribute("aria-describedby", /login-username-error/);
  await expect(username).toBeFocused();
  await expectNoSeriousViolations(page, "Login mit Validierungsfehlern");
});

test("Typing-Zustände behalten Fokus und erfüllen Axe", async ({ page }) => {
  await login(page, "browser.accessibility.typing");
  await page.goto("/spielen");
  const input = page.locator("[data-input]");
  const target = page.locator("[data-target]");
  await expect(input).toBeEnabled({ timeout: 15_000 });
  await expectNoSeriousViolations(page, "Typing vorbereitet");

  const zenToggle = page.locator("[data-zen-toggle]");
  await zenToggle.click();
  await expect(zenToggle).toHaveAccessibleName("Zen-Modus beenden");
  await expectNoSeriousViolations(page, "Typing im Zen-Modus");
  await page.keyboard.press("Escape");
  await expect(zenToggle).toHaveAccessibleName("Zen-Modus");

  const targetText = ((await target.textContent()) || "").trim();
  await input.fill(Array.from(targetText).slice(0, 3).join(""));
  await expect(input).toBeFocused();
  await expectNoSeriousViolations(page, "Typing laufend");

  await input.fill(targetText);
  await expect(page.locator(".finish-panel")).toBeVisible({ timeout: 15_000 });
  await expectNoSeriousViolations(page, "Typing Ergebnis");
});

test("Typing setzt einen fehlgeschlagenen Rundenstart zurück und bietet Retry", async ({ page }) => {
  await login(page, "browser.accessibility.typing-retry");
  let failNextStart = true;
  await page.route("**/api/spielen/start", async (route) => {
    if (failNextStart) {
      failNextStart = false;
      await route.abort("failed");
      return;
    }
    await route.continue();
  });

  await page.goto("/spielen");
  const root = page.locator("[data-typing-app]").first();
  const retry = root.locator("[data-start]");
  const liveRegion = root.locator("[data-typing-live-region]");
  await expect(retry).toHaveText("Erneut versuchen");
  await expect(retry).toBeEnabled();
  await expect(root).not.toHaveAttribute("aria-busy", "true");
  await expect(liveRegion).toContainText("versuche es erneut");

  await retry.click();
  await expect(root.locator("[data-input]")).toBeEnabled({ timeout: 15_000 });
  await expect(liveRegion).toContainText("Runde bereit");
  await expectNoSeriousViolations(page, "Typing nach Netzwerk-Retry");
});

test("Arena-Zustände sind per Tastatur, mobil sowie in Dark und Light zugänglich", async ({ page }, testInfo) => {
  testInfo.setTimeout(120_000);
  await login(page, `browser.accessibility.arena.${testInfo.workerIndex}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("Accessibility Arena");
  await page.getByLabel("Maximale Teilnehmer").fill("2");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  await expect(page).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
  const roomUrl = page.url();

  let endpointState = "Pending";
  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));
  await page.route("**/api/arena/*/speicherstatus", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ state: endpointState })
  }));

  await page.goto(`${roomUrl}?arenaTest=running&a11y=dark`);
  const root = page.locator("[data-arena-room]");
  const target = page.locator("[data-arena-target]");
  const input = page.locator("[data-arena-input]");
  await expect(root).toHaveAttribute("data-connection-state", "connected");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(target).toHaveAttribute("role", "region");
  await expect(target).toHaveAttribute("tabindex", "0");
  await expect(target).toHaveAccessibleName("Zieltext des Arena-Rennens");
  await expect(page.locator("[data-live-preview]")).toHaveAttribute("tabindex", "0");
  const rosterExpand = page.locator("[data-arena-roster-expand]");
  const rosterSearch = page.locator("[data-arena-roster-search]");
  await expect(rosterExpand).toBeVisible();
  await rosterSearch.fill("kein vorhandener name");
  await expect(root).toHaveAttribute("data-arena-roster-expanded", "true");
  await expect(rosterExpand).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("[data-arena-participants] tr:visible")).toHaveCount(0);
  await expect(page.locator("[data-arena-roster-status]")).toContainText("0 von");
  await rosterSearch.fill("");
  await expect(page.locator("[data-arena-participants] tr:visible")).toHaveCount(1);
  await rosterExpand.click();
  await expect(root).toHaveAttribute("data-arena-roster-expanded", "false");
  await expectNoSeriousViolations(page, "Arena Running Dark Desktop");

  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  const zoomLayout = await page.evaluate(() => {
    const target = document.querySelector("[data-arena-target]");
    const input = document.querySelector("[data-arena-input]");
    const persistence = document.querySelector("[data-arena-persistence-status]");
    return {
      zoom: document.documentElement.style.zoom,
      horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
      targetTextLength: target?.textContent?.trim().length || 0,
      persistenceTextLength: persistence?.textContent?.trim().length || 0,
      targetRenderedWidth: target?.clientWidth || 0,
      inputRenderedWidth: input?.clientWidth || 0
    };
  });
  expect(zoomLayout).toEqual({
    zoom: "2",
    horizontalOverflow: false,
    targetTextLength: expect.any(Number),
    persistenceTextLength: expect.any(Number),
    targetRenderedWidth: expect.any(Number),
    inputRenderedWidth: expect.any(Number)
  });
  expect(zoomLayout.targetTextLength).toBeGreaterThan(100);
  expect(zoomLayout.persistenceTextLength).toBeGreaterThan(10);
  expect(zoomLayout.targetRenderedWidth).toBeGreaterThan(120);
  expect(zoomLayout.inputRenderedWidth).toBeGreaterThan(120);
  await target.focus();
  await page.keyboard.press("Tab");
  await expect(input).toBeFocused();
  await page.evaluate(() => {
    document.documentElement.style.zoom = "";
  });

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".arena-page-header .lead")).toBeVisible();
  await expect(page.locator(".arena-page-header .room-code-share")).toBeVisible();
  await expect(page.locator(".arena-page-header .room-code")).toBeVisible();
  await expect(page.locator(".arena-page-header [data-share-title]")).toBeVisible();
  await expect(page.locator(".arena-phase-steps")).toBeVisible();
  await expect(page.locator(".arena-phase-steps [aria-current='step']")).toHaveCount(1);
  const zenOverlap = await page.evaluate(() => {
    const zen = document.querySelector("[data-zen-toggle]")?.getBoundingClientRect();
    const actions = [...document.querySelectorAll(".mobile-topbar-actions button")]
      .map((button) => button.getBoundingClientRect());
    if (!zen) {
      return true;
    }
    return actions.some((action) => !(
      zen.right <= action.left || zen.left >= action.right ||
      zen.bottom <= action.top || zen.top >= action.bottom));
  });
  expect(zenOverlap).toBe(false);
  const mobileScreenshot = testInfo.outputPath("arena-accessibility-mobile-390x844.png");
  await page.screenshot({ path: mobileScreenshot, fullPage: true, animations: "disabled" });
  await testInfo.attach("Arena Mobile 390x844", { path: mobileScreenshot, contentType: "image/png" });

  await page.setViewportSize({ width: 320, height: 568 });
  await expect(page.locator(".arena-page-header .room-code-share")).toBeVisible();
  await expect(page.locator(".arena-phase-steps")).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  const narrowScreenshot = testInfo.outputPath("arena-accessibility-mobile-320x568.png");
  await page.screenshot({ path: narrowScreenshot, fullPage: true, animations: "disabled" });
  await testInfo.attach("Arena Mobile 320x568", { path: narrowScreenshot, contentType: "image/png" });

  await page.setViewportSize({ width: 390, height: 844 });
  await target.focus();
  await expect(target).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect.poll(() => target.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  await page.keyboard.press("Tab");
  await expect(input).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(page.locator("[data-arena-dnf]")).toBeFocused();
  await expectNoSeriousViolations(page, "Arena Running Dark Mobile");

  await page.locator(".mobile-topbar [data-theme-toggle]").click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await page.goto(`${roomUrl}?arenaTest=pending&a11y=pending`);
  await expect(root).toHaveAttribute("data-persistence-state", "Pending");
  await expectNoSeriousViolations(page, "Arena Pending Light Mobile");

  endpointState = "Persisted";
  await expect(root).toHaveAttribute("data-persistence-state", "Persisted", { timeout: 10_000 });
  await expectNoSeriousViolations(page, "Arena Persisted Light Mobile");

  endpointState = "Failed";
  await page.goto(`${roomUrl}?arenaTest=pending&a11y=failed`);
  await expect(root).toHaveAttribute("data-persistence-state", "Failed");
  await expect(page.locator("[data-arena-persistence-status]")).toContainText("nicht vergeben");
  await expectNoSeriousViolations(page, "Arena Failed Light Mobile");
});

test("Privacy-Bestätigungen erfüllen Axe auf Desktop und Mobile", async ({ page }) => {
  await login(page, "browser.accessibility.privacy");
  for (const route of ["/profil/statistik-zuruecksetzen", "/profil/loeschen", "/profil/export"]) {
    await page.goto(route);
    await expectNoSeriousViolations(page, `Privacy ${route} Desktop`);
    await page.setViewportSize({ width: 320, height: 568 });
    await expectNoSeriousViolations(page, `Privacy ${route} Mobile`);
    await page.setViewportSize({ width: 1366, height: 768 });
  }
});
