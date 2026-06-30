const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
  await expect(page.locator(".sidebar-profile")).toContainText(displayName(username));
}

function displayName(username) {
  return username
    .replace(/[._]/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1).toLowerCase()}`)
    .join(" ");
}

function firstGraphemes(value, count) {
  return Array.from(value).slice(0, count).join("");
}

function firstStableGraphemes(value, count) {
  const graphemes = Array.from(value);
  let length = Math.min(count, graphemes.length);
  while (length < graphemes.length && /\s/u.test(graphemes[length - 1])) {
    length += 1;
  }

  return graphemes.slice(0, length).join("");
}

function wrongCharacterFor(value) {
  return value === "x" ? "y" : "x";
}

async function expectNoHorizontalOverflow(page) {
  const overflow = await page.evaluate(() => {
    const documentWidth = document.documentElement.scrollWidth;
    const viewportWidth = document.documentElement.clientWidth;
    const offenders = [...document.body.querySelectorAll("*")]
      .filter((element) => element instanceof HTMLElement && element.getBoundingClientRect().right > viewportWidth + 1)
      .slice(0, 5)
      .map((element) => element.className || element.tagName);

    return { documentWidth, viewportWidth, offenders };
  });

  expect(overflow.documentWidth, `Overflow auf ${page.url()} durch: ${overflow.offenders.join(", ")}`).toBeLessThanOrEqual(overflow.viewportWidth + 1);
}

async function expectOfflineRuntimeAssets(page) {
  const audit = await page.evaluate(() => {
    const origin = window.location.origin;
    const domUrls = [...document.querySelectorAll("img[src], script[src], link[href], svg use[href]")]
      .map((element) => {
        const raw = element.getAttribute("src") || element.getAttribute("href") || "";
        const absolute = element.src || element.href || raw;
        return { raw, absolute };
      });
    const resourceUrls = performance.getEntriesByType("resource")
      .map((entry) => ({ raw: entry.name, absolute: entry.name }));
    const all = [...domUrls, ...resourceUrls];
    const external = all
      .filter((item) => /^https?:\/\//i.test(item.absolute))
      .filter((item) => new URL(item.absolute).origin !== origin)
      .map((item) => item.absolute);
    const keywarsIconUses = [...document.querySelectorAll("svg use")]
      .filter((item) => (item.getAttribute("href") || "").startsWith("/vendor/keywars-assets/keywars-icons.svg#kw-"))
      .length;
    const localIllustrations = [...document.querySelectorAll("img[src*='/vendor/keywars-assets/illustrations/'], img[src^='/vendor/keywars-assets/illustrations/']")]
      .length;
    return {
      external: [...new Set(external)],
      keywarsIconUses,
      localIllustrations
    };
  });

  expect(audit.external, `Externe Runtime-Assets auf ${page.url()}`).toEqual([]);
  expect(audit.keywarsIconUses).toBeGreaterThan(0);
}

async function expectCompactMobileHeader(page) {
  const header = await page.locator(".mobile-topbar").evaluate((topbar) => {
    const bounds = topbar.getBoundingClientRect();
    const firstPanel = document.querySelector(".status-cockpit")?.getBoundingClientRect();
    return {
      height: Math.round(bounds.height),
      firstPanelTop: Math.round(firstPanel?.top ?? 9999),
      viewportHeight: window.innerHeight
    };
  });

  expect(header.height).toBeLessThanOrEqual(64);
  expect(header.firstPanelTop).toBeLessThan(header.viewportHeight * 0.16);
}

async function expectResponsiveAppShell(page, width) {
  await expect(page.locator(".desktop-sidebar")).toBeHidden();
  await expect(page.locator(".desktop-topbar")).toBeHidden();
  await expect(page.locator(".mobile-topbar")).toBeVisible();
  await expect(page.locator(".mobile-bottom-nav")).toBeVisible();
  await expectNoHorizontalOverflow(page);

  const metrics = await page.evaluate(() => {
    const shell = document.querySelector(".shell");
    const main = document.querySelector(".app-main");
    const shellBounds = shell?.getBoundingClientRect();
    const mainBounds = main?.getBoundingClientRect();
    return {
      viewportWidth: window.innerWidth,
      shellWidth: Math.round(shellBounds?.width ?? 0),
      shellLeft: Math.round(shellBounds?.left ?? 0),
      shellRightGap: Math.round(window.innerWidth - (shellBounds?.right ?? 0)),
      mainLeft: Math.round(mainBounds?.left ?? 0)
    };
  });

  expect(metrics.mainLeft).toBeLessThanOrEqual(2);
  expect(metrics.shellWidth).toBeGreaterThanOrEqual(width >= 768 ? 700 : width - 24);
  expect(Math.abs(metrics.shellLeft - metrics.shellRightGap)).toBeLessThanOrEqual(18);
}

async function expectArenaCoreInFirstView(page) {
  const layout = await page.evaluate(() => {
    const timer = document.querySelector("[data-arena-timer]")?.getBoundingClientRect();
    const target = document.querySelector("[data-arena-target]")?.getBoundingClientRect();
    const input = document.querySelector("[data-arena-input]")?.getBoundingClientRect();
    if (!timer || !target || !input) {
      throw new Error("Arena-Kern fehlt.");
    }

    return {
      viewportHeight: window.innerHeight,
      timerTop: Math.round(timer.top),
      targetTop: Math.round(target.top),
      inputBottom: Math.round(input.bottom)
    };
  });

  expect(layout.timerTop).toBeLessThan(layout.viewportHeight * 0.32);
  expect(layout.targetTop).toBeLessThan(layout.viewportHeight * 0.52);
  expect(layout.inputBottom).toBeLessThan(layout.viewportHeight - 76);
}

async function expectArenaConnected(page) {
  await expect(page.locator("[data-arena-connection-quality]")).toHaveText(/Verbindung: (aktiv|neu verbunden)/, { timeout: 15_000 });
  await expect(page.getByText("Arena-Verbindung ist nicht aktiv.")).toHaveCount(0);
}

async function expectMobilePageEndClearOfBottomNav(page) {
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await page.waitForTimeout(100);
  const clearance = await page.evaluate(() => {
    const nav = document.querySelector(".mobile-bottom-nav");
    const main = document.querySelector(".app-main");
    if (!(nav instanceof HTMLElement) || !(main instanceof HTMLElement)) {
      throw new Error("Mobile shell is missing.");
    }

    const visibleElements = [...main.querySelectorAll("button, a, input, select, textarea, .card, .panel, .competition-main, .competition-card")]
      .filter((element) => element instanceof HTMLElement)
      .filter((element) => {
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
      });
    const last = visibleElements
      .sort((left, right) => left.getBoundingClientRect().bottom - right.getBoundingClientRect().bottom)
      .at(-1);
    const lastRect = last?.getBoundingClientRect();
    const navRect = nav.getBoundingClientRect();
    return {
      lastBottom: Math.round(lastRect?.bottom ?? 0),
      navTop: Math.round(navRect.top),
      lastText: (last?.textContent || last?.getAttribute("aria-label") || last?.getAttribute("name") || "").trim().replace(/\s+/g, " ").slice(0, 80)
    };
  });

  expect(clearance.lastBottom, `Letztes Element wird von Bottom-Nav verdeckt: ${clearance.lastText}`).toBeLessThanOrEqual(clearance.navTop - 4);
}

async function expectArenaCreateFormBeforePreview(page) {
  const order = await page.evaluate(() => {
    const form = document.querySelector(".arena-create-form")?.getBoundingClientRect();
    const preview = document.querySelector(".arena-create-preview")?.getBoundingClientRect();
    const titleInput = document.querySelector(".arena-create-form input[name='Input.Title']")?.getBoundingClientRect();
    if (!form || !preview || !titleInput) {
      throw new Error("Arena-Erstellen-Formular oder Vorschau fehlt.");
    }

    return {
      formTop: Math.round(form.top),
      previewTop: Math.round(preview.top),
      titleInputBottom: Math.round(titleInput.bottom),
      viewportHeight: window.innerHeight
    };
  });

  expect(order.formTop).toBeLessThan(order.previewTop);
  expect(order.titleInputBottom).toBeLessThan(order.viewportHeight * 0.58);
}

test("Dashboard und Einstellungen rendern im echten Browser", async ({ page }) => {
  await login(page, "browser.smoke");

  await expect(page.locator(".desktop-sidebar")).toBeVisible();
  await expect(page.locator(".status-cockpit")).toBeVisible();
  await expect(page.locator(".dashboard-quick-round")).toBeVisible();
  await expect(page.locator(".daily-board-panel")).toBeVisible();
  await expect(page.locator(".quest-card").first()).toBeVisible();
  await expect(page.locator(".recent-results-panel")).toBeVisible();
  await expectOfflineRuntimeAssets(page);
  await expectNoHorizontalOverflow(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await expectCompactMobileHeader(page);
  await expectResponsiveAppShell(page, 390);
  await expect(page.locator(".mobile-bottom-nav")).toBeVisible();
  await expect(page.locator(".dashboard-quick-round")).toBeVisible();
  await expect(page.locator(".daily-board-panel")).toBeVisible();
  await expectNoHorizontalOverflow(page);
  await page.goto("/profil/einstellungen");
  await expectCompactMobileHeader(page);
  await expect(page.getByRole("heading", { name: "Einstellungen" })).toBeVisible();
  await expect(page.getByText("Identität aus AD/LDAP")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "de");
});

test("Designmodus-Button toggelt sichtbaren Shell-Zustand", async ({ page }) => {
  await login(page, "browser.design");

  const designButton = page.locator(".desktop-topbar [data-design-mode-toggle]");
  await expect(designButton).toBeVisible();
  await expect(designButton).toHaveAttribute("aria-pressed", "false");
  await expect(page.locator("body")).not.toHaveClass(/app-design-mode/);

  await designButton.click();
  await expect(designButton).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("body")).toHaveClass(/app-design-mode/);
  await expect(designButton).toHaveCSS("color", "rgb(4, 16, 25)");

  await page.reload();
  await expect(page.locator("body")).toHaveClass(/app-design-mode/);
  await expect(page.locator(".desktop-topbar [data-design-mode-toggle]")).toHaveAttribute("aria-pressed", "true");

  await page.locator(".desktop-topbar [data-design-mode-toggle]").click();
  await expect(page.locator("body")).not.toHaveClass(/app-design-mode/);
  await expect(page.locator(".desktop-topbar [data-design-mode-toggle]")).toHaveAttribute("aria-pressed", "false");
});

test("Sidebar-Navigation hält lange Labels im aktiven Button", async ({ page }) => {
  await login(page, "browser.sidebar.nav");
  await page.goto("/herausforderungen");
  await expect(page.getByRole("heading", { name: "Herausforderungen" })).toBeVisible();

  const metrics = await page.locator(".desktop-sidebar").evaluate((sidebar) => {
    const activeLink = sidebar.querySelector(".sidebar-nav a.active");
    const label = activeLink?.querySelector("span");
    if (!(activeLink instanceof HTMLElement) || !(label instanceof HTMLElement)) {
      throw new Error("Aktiver Sidebar-Link fehlt.");
    }

    const sidebarBounds = sidebar.getBoundingClientRect();
    const linkBounds = activeLink.getBoundingClientRect();
    return {
      labelText: label.textContent?.trim(),
      linkTitle: activeLink.getAttribute("title"),
      labelTitle: label.getAttribute("title"),
      labelClientWidth: Math.ceil(label.clientWidth),
      labelScrollWidth: Math.ceil(label.scrollWidth),
      linkClientWidth: Math.ceil(activeLink.clientWidth),
      linkScrollWidth: Math.ceil(activeLink.scrollWidth),
      linkRight: Math.ceil(linkBounds.right),
      sidebarRight: Math.floor(sidebarBounds.right)
    };
  });

  expect(metrics.labelText).toBe("Herausforderungen");
  expect(metrics.linkTitle).toBe("Herausforderungen");
  expect(metrics.labelClientWidth, "Aktiver Sidebar-Button hat keine sichtbare Label-Fläche.").toBeGreaterThan(0);
  expect(metrics.linkScrollWidth, "Aktiver Sidebar-Button hat lokalen Horizontal-Overflow.").toBeLessThanOrEqual(metrics.linkClientWidth + 1);
  expect(metrics.linkRight, "Aktiver Sidebar-Button läuft über die Sidebar-Kante.").toBeLessThanOrEqual(metrics.sidebarRight - 12);
});

test("Quickstart-Karten nutzen volle Hover-Texte ohne Pfeilzeichen", async ({ page }) => {
  await login(page, "browser.quickstart.hover");
  await expect(page.locator(".quickstart-panel")).toBeVisible();

  const audit = await page.locator(".quickstart-panel").evaluate((panel) => {
    const cards = [...panel.querySelectorAll(".quickstart-card")].map((card) => {
      const title = card.querySelector("strong");
      const subtitle = card.querySelector("small");
      return {
        titleText: title?.textContent?.trim(),
        subtitleText: subtitle?.textContent?.trim(),
        hoverText: card.getAttribute("title"),
        hasArrowSlot: Boolean(card.querySelector(".quickstart-arrow")),
        rawText: card.textContent?.replace(/\s+/g, " ").trim(),
        titleClientWidth: Math.ceil(title?.clientWidth ?? 0),
        titleScrollWidth: Math.ceil(title?.scrollWidth ?? 0),
        titleClientHeight: Math.ceil(title?.clientHeight ?? 0),
        subtitleClientWidth: Math.ceil(subtitle?.clientWidth ?? 0),
        subtitleScrollWidth: Math.ceil(subtitle?.scrollWidth ?? 0)
      };
    });

    return cards;
  });

  expect(audit).toHaveLength(6);
  expect(audit.map((card) => card.titleText)).toContain("Herausforderung");
  expect(audit.every((card) => card.hoverText?.includes(card.titleText))).toBe(true);
  expect(audit.every((card) => card.hasArrowSlot)).toBe(false);
  expect(audit.every((card) => !/[›>]/u.test(card.rawText || ""))).toBe(true);
  for (const card of audit) {
    expect(card.titleScrollWidth, `${card.titleText} ist im Quickstart-Titel abgeschnitten.`).toBeLessThanOrEqual(card.titleClientWidth + 1);
    expect(card.titleClientHeight, `${card.titleText} bricht im Quickstart-Titel unruhig um.`).toBeLessThanOrEqual(24);
    expect(card.subtitleScrollWidth, `${card.subtitleText} ist im Quickstart-Untertitel abgeschnitten.`).toBeLessThanOrEqual(card.subtitleClientWidth + 1);
  }

  await expectNoHorizontalOverflow(page);
});

test("Offline-Visuals zeigen Achievement-Katalog und Mission-Icons", async ({ page }) => {
  await login(page, "browser.visual.assets");

  await page.goto("/profil/erfolge");
  await expect(page.getByRole("heading", { name: "Erfolge", exact: true })).toBeVisible();
  expect(await page.locator(".achievement-card").count()).toBeGreaterThan(20);
  await expect(page.locator(".achievement-card.is-locked").first()).toBeVisible();
  await expect(page.locator("img[src*='motivator-achievements.svg']")).toBeVisible();
  await expectOfflineRuntimeAssets(page);
  await expectNoHorizontalOverflow(page);

  await page.goto("/profil/ziele");
  await expect(page.getByRole("heading", { name: "Ziele" })).toBeVisible();
  await expect(page.locator(".quest-card .quest-icon .kw-icon").first()).toBeVisible();
  await expectOfflineRuntimeAssets(page);
  await expectNoHorizontalOverflow(page);
});

test("App-Shell nutzt Mobile- und Tablet-Breakpoints ohne tote Fläche", async ({ page }) => {
  await login(page, "browser.shell.breakpoints");
  const routes = ["/", "/spielen", "/ranglisten", "/arena", "/texte", "/tageschallenge", "/profil/einstellungen"];

  for (const viewport of [
    { width: 390, height: 844 },
    { width: 768, height: 1024 }
  ]) {
    await page.setViewportSize(viewport);
    for (const route of routes) {
      await page.goto(route);
      await expectResponsiveAppShell(page, viewport.width);
    }
  }
});

test("Mobile Bottom-Navigation verdeckt keine Formular- oder Trainingsenden", async ({ page }) => {
  await login(page, "browser.bottom.nav.clearance");
  await page.setViewportSize({ width: 390, height: 844 });

  for (const route of ["/arena", "/arena/neu", "/spielen/sprint", "/spielen/fehlerfokus", "/texte/sammlungen/neu", "/herausforderungen/neu"]) {
    await page.goto(route);
    await expectResponsiveAppShell(page, 390);
    if (route === "/arena/neu") {
      await expectArenaCreateFormBeforePreview(page);
    }
    await expectMobilePageEndClearOfBottomNav(page);
    await expectNoHorizontalOverflow(page);
  }
});

test("Tageschallenge startet aus einem klaren manuellen Zustand", async ({ page }) => {
  await login(page, "browser.daily.challenge");
  await page.goto("/tageschallenge");
  await expect(page.getByRole("heading", { name: "Tageschallenge" })).toBeVisible();
  await expect(page.locator(".daily-challenge-card.typing-idle")).toBeVisible();
  await expect(page.getByRole("button", { name: "Tageschallenge starten" })).toBeVisible();
  await expect(page.locator(".daily-challenge-card .typing-timer")).toBeHidden();
  await expect(page.locator(".daily-challenge-card [data-input]")).toBeHidden();
  await expectNoHorizontalOverflow(page);

  await page.getByRole("button", { name: "Tageschallenge starten" }).click();
  await expect(page.locator(".daily-challenge-card")).not.toHaveClass(/typing-idle/);
  await expect(page.locator(".daily-challenge-card .typing-timer")).toBeVisible();
  await expect(page.locator(".daily-challenge-card [data-input]")).toBeVisible();
  await expect(page.locator(".daily-challenge-card [data-input]")).toBeEnabled();
  await expect(page.getByRole("button", { name: "Lauf aktiv" })).toBeHidden();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/tageschallenge");
  await expectCompactMobileHeader(page);
  await expectResponsiveAppShell(page, 390);
  await expect(page.locator(".daily-challenge-card.typing-idle")).toBeVisible();
  await expectNoHorizontalOverflow(page);
});

test("Vorbereitete Sofortrunden zeigen keinen stehenden Countdown oder tote Werte", async ({ page }) => {
  await login(page, "browser.prepared.timer");
  await page.setViewportSize({ width: 390, height: 844 });

  await page.goto("/");
  await expect(page.locator(".dashboard-quick-round.typing-prepared")).toBeVisible({ timeout: 15_000 });
  const dashboardPrepared = await page.locator(".dashboard-quick-round").evaluate((card) => {
    const timer = card.querySelector(".typing-timer");
    const timerStrong = timer?.querySelector("strong");
    const footer = card.querySelector(".dashboard-round-footer");
    return {
      timerText: timer?.textContent || "",
      timerValueVisible: timerStrong instanceof HTMLElement && getComputedStyle(timerStrong).display !== "none",
      footerVisible: footer instanceof HTMLElement && getComputedStyle(footer).display !== "none"
    };
  });
  expect(dashboardPrepared.timerText).not.toMatch(/\d{2}:\d{2}\.\d/u);
  expect(dashboardPrepared.timerValueVisible).toBe(false);
  expect(dashboardPrepared.footerVisible).toBe(false);

  await page.goto("/spielen");
  await expect(page.locator(".play-quickstart.typing-prepared")).toBeVisible({ timeout: 15_000 });
  const playPrepared = await page.locator(".play-quickstart").evaluate((card) => {
    const timer = card.querySelector(".typing-timer");
    const timerStrong = timer?.querySelector("strong");
    return {
      timerText: timer?.textContent || "",
      timerValueVisible: timerStrong instanceof HTMLElement && getComputedStyle(timerStrong).display !== "none"
    };
  });
  expect(playPrepared.timerText).not.toMatch(/\d{2}:\d{2}\.\d/u);
  expect(playPrepared.timerValueVisible).toBe(false);
  await expectNoHorizontalOverflow(page);
});

test("Logout nutzt eine neutrale Public-Shell ohne private Statusdaten", async ({ page }) => {
  await login(page, "browser.logout.public");
  await page.goto("/abmelden");
  await expect(page.getByRole("heading", { name: "KeyWars verlassen" })).toBeVisible();
  await expect(page.locator(".status-cockpit")).toHaveCount(0);
  await expect(page.locator(".desktop-sidebar")).toHaveCount(0);
  await expect(page.locator(".mobile-bottom-nav")).toHaveCount(0);
  await expect(page.getByText("Tage Streak")).toHaveCount(0);
  await expectNoHorizontalOverflow(page);

  await page.getByRole("button", { name: "Jetzt abmelden" }).click();
  await expect(page).toHaveURL(/\/abmelden\?abgemeldet=1$/);
  await expect(page.getByRole("heading", { name: "Du bist abgemeldet" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Wieder anmelden" })).toBeVisible();
  await expect(page.locator(".status-cockpit")).toHaveCount(0);
  await expect(page.locator(".desktop-sidebar")).toHaveCount(0);
  await expect(page.locator(".mobile-bottom-nav")).toHaveCount(0);
});

test("Herausforderungen haben einen handlungsfähigen Empty State", async ({ page }) => {
  await login(page, "browser.challenge.empty");
  await page.goto("/herausforderungen");
  await expect(page.locator(".challenge-empty")).toBeVisible();
  await expect(page.getByRole("link", { name: "Gruppe herausfordern" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Vorher trainieren" })).toBeVisible();
  await expectNoHorizontalOverflow(page);
});

test("Challenge-Spiel bleibt nach vorbereiteter Runde und Reload spielbar", async ({ page, browser, baseURL }, testInfo) => {
  const suffix = `${testInfo.workerIndex}.${Date.now()}`;
  const partnerContext = await browser.newContext({ baseURL, colorScheme: "dark", reducedMotion: "reduce" });
  try {
    const partner = await partnerContext.newPage();
    await login(partner, `browser.challenge.partner.${suffix}`);

    await login(page, `browser.challenge.owner.${suffix}`);
    await page.goto("/herausforderungen/neu");
    await page.getByLabel("Titel").fill(`Browser Challenge ${suffix}`);
    const firstPerson = page.locator('fieldset input[type="checkbox"]').first();
    await expect(firstPerson).toBeVisible();
    await firstPerson.check();
    await page.getByRole("button", { name: "Herausforderung senden" }).click();
    await expect(page).toHaveURL(/\/herausforderungen\/[0-9a-f-]{36}$/i);

    await page.getByRole("link", { name: "Runde spielen" }).click();
    await expect(page).toHaveURL(/\/herausforderungen\/[0-9a-f-]{36}\/spielen$/i);
    await expect(page.locator("[data-input]")).toBeEnabled({ timeout: 15_000 });
    await expect(page.locator("[data-target]")).not.toContainText("konnte nicht vorbereitet werden");

    await page.reload();
    await expect(page.locator("[data-input]")).toBeEnabled({ timeout: 15_000 });
    await expect(page.locator("[data-target]")).not.toContainText("konnte nicht vorbereitet werden");
    await expectNoHorizontalOverflow(page);

    const target = (await page.locator("[data-target]").textContent()).trim();
    await page.setViewportSize({ width: 462, height: 720 });
    await page.locator("[data-input]").fill(target);
    await expect(page.locator(".finish-panel")).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".xp-reveal")).toBeVisible();
    await expect(page.locator(".typing-analysis")).toBeVisible();
    await expect(page.locator(".challenge-typing-card.typing-finished > .typing-target")).toBeHidden();
    await expect(page.locator(".challenge-typing-card.typing-finished > .typing-timer")).toBeHidden();
    await expect(page.locator(".challenge-typing-card.typing-finished > label")).toBeHidden();
    await expect(page.locator(".challenge-typing-card.typing-finished > [data-start]")).toBeHidden();
    await expectNoHorizontalOverflow(page);

    const challengeLayout = await page.evaluate(() => {
      const viewportWidth = document.documentElement.clientWidth;
      const card = document.querySelector(".challenge-typing-card")?.getBoundingClientRect();
      const metricColumns = getComputedStyle(document.querySelector(".challenge-play-page .result-metrics")).gridTemplateColumns
        .split(" ")
        .filter(Boolean)
        .length;
      const motivationEvents = document.querySelector(".challenge-play-page .motivation-events");
      const motivationColumns = motivationEvents
        ? getComputedStyle(motivationEvents).gridTemplateColumns.split(" ").filter(Boolean).length
        : 0;

      return {
        cardRight: Math.round(card?.right ?? 0),
        viewportWidth,
        metricColumns,
        motivationColumns
      };
    });
    expect(challengeLayout.cardRight).toBeLessThanOrEqual(challengeLayout.viewportWidth);
    expect(challengeLayout.metricColumns).toBe(2);
    expect(challengeLayout.motivationColumns).toBeLessThanOrEqual(2);

    await page.goto("/ranglisten?board=challenge&period=day");
    await expect(page.getByRole("heading", { name: "Challenge-Bestleistungen" })).toBeVisible();
    await expect(page.locator(".podium-card")).toHaveCount(1);
    await expect(page.locator(".competition-table tbody tr")).toHaveCount(1);
    await expect(page.locator(".competition-table")).toContainText("Platz offen");
    await expect(page.locator(".competition-side")).toContainText("Top halten");
    await expect(page.locator(".competition-side")).toContainText("Du führst dieses Board");
    await expect(page.locator(".competition-side")).not.toContainText("Bestwert setzen");
    await expectNoHorizontalOverflow(page);
  } finally {
    await partnerContext.close();
  }
});

test("Textbibliothek bleibt auf Desktop und Mobile sauber ausgerichtet", async ({ page }) => {
  await login(page, "browser.texts.ui");
  await page.goto("/texte");
  await expect(page.locator(".filter-panel")).toBeVisible();
  await expect(page.locator(".text-card").first()).toBeVisible();
  await expect(page.locator(".text-card").first().locator(".card-actions")).toBeVisible();
  await expectNoHorizontalOverflow(page);

  const desktopSpacing = await page.locator(".text-card").first().locator(".card-actions").evaluate((actions) => {
    const buttons = [...actions.querySelectorAll("a,button")].map((item) => item.getBoundingClientRect());
    return {
      count: buttons.length,
      gap: buttons.length >= 2 ? Math.round(buttons[1].left - buttons[0].right) : 99,
      sameRow: buttons.length < 2 || Math.abs(buttons[0].top - buttons[1].top) < 2
    };
  });
  expect(desktopSpacing.count).toBeGreaterThanOrEqual(2);
  expect(desktopSpacing.gap).toBeGreaterThanOrEqual(8);
  expect(desktopSpacing.sameRow).toBe(true);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/texte");
  await expectCompactMobileHeader(page);
  await expectResponsiveAppShell(page, 390);
  await expect(page.locator(".mobile-training-priority")).toBeVisible();
  await expect(page.locator(".mobile-training-priority").getByRole("link", { name: "Trainieren" }).first()).toBeVisible();
  await expect(page.locator(".filter-panel")).toBeVisible();
  await expect(page.locator(".text-card").first()).toBeVisible();
  const mobilePriority = await page.evaluate(() => {
    const priority = document.querySelector(".mobile-training-priority");
    const train = priority?.querySelector("a[href*='/spielen/text']");
    const filter = document.querySelector(".filter-panel");
    const priorityBounds = priority?.getBoundingClientRect();
    const trainBounds = train?.getBoundingClientRect();
    const filterBounds = filter?.getBoundingClientRect();
    return {
      priorityTop: Math.round(priorityBounds?.top ?? 9999),
      trainBottom: Math.round(trainBounds?.bottom ?? 9999),
      filterTop: Math.round(filterBounds?.top ?? 0),
      safeBottom: Math.round(window.innerHeight - 88)
    };
  });
  expect(mobilePriority.priorityTop).toBeLessThan(mobilePriority.filterTop);
  expect(mobilePriority.trainBottom).toBeLessThan(mobilePriority.safeBottom);
  await expectNoHorizontalOverflow(page);
});

test("Spielseite zeigt Sofortrunde und Modi sauber auf Desktop und Mobile", async ({ page }) => {
  await login(page, "browser.play.ui");
  await page.goto("/spielen");
  await expect(page.locator(".play-quickstart")).toBeVisible();
  await expect(page.locator(".play-mode-link")).toHaveCount(4);
  await expect(page.locator(".play-text-card").first()).toBeVisible();
  await expectNoHorizontalOverflow(page);

  const desktopPosition = await page.evaluate(() => {
    const quickstart = document.querySelector(".play-quickstart");
    const heroCopy = document.querySelector(".play-hero-copy");
    if (!quickstart) {
      throw new Error("Sofortrunde fehlt.");
    }

    const bounds = quickstart.getBoundingClientRect();
    const after = heroCopy ? getComputedStyle(heroCopy, "::after") : null;
    return {
      top: Math.round(bounds.top),
      pseudoDisplay: after?.display ?? "absent",
      pseudoContent: after?.content ?? "absent",
      viewportHeight: window.innerHeight
    };
  });
  expect(["absent", "none"]).toContain(desktopPosition.pseudoDisplay);
  expect(["absent", "none"]).toContain(desktopPosition.pseudoContent);
  expect(desktopPosition.top).toBeLessThan(desktopPosition.viewportHeight);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/spielen");
  await expectCompactMobileHeader(page);
  await expectResponsiveAppShell(page, 390);
  await expect(page.locator(".play-quickstart")).toBeVisible();
  await expect(page.locator(".play-mode-link")).toHaveCount(4);
  await expectNoHorizontalOverflow(page);

  const mobileLayout = await page.evaluate(() => {
    const quickstart = document.querySelector(".play-quickstart");
    const target = document.querySelector(".play-target");
    const hasButtonOverflow = [...document.querySelectorAll("button, .button, .play-mode-link")]
      .some((element) => element.scrollWidth > element.clientWidth + 1);

    return {
      quickstartTop: Math.round(quickstart?.getBoundingClientRect().top ?? 9999),
      targetTop: Math.round(target?.getBoundingClientRect().top ?? 9999),
      viewportHeight: window.innerHeight,
      hasButtonOverflow
    };
  });
  expect(mobileLayout.quickstartTop).toBeLessThan(mobileLayout.viewportHeight);
  expect(mobileLayout.targetTop).toBeLessThan(mobileLayout.viewportHeight);
  expect(mobileLayout.hasButtonOverflow).toBe(false);
});

test("Wettbewerbsseite bleibt responsiv und respektiert Ranglisten-Sichtbarkeit", async ({ page }) => {
  await login(page, "browser.competition.ui");
  await page.goto("/ranglisten?board=sprint&period=day&mode=sprint60");
  await expect(page.getByRole("heading", { name: "Wettbewerb" })).toBeVisible();
  await expect(page.locator(".competition-chase-line")).toBeVisible();
  await expect(page.locator(".competition-tabs a")).toHaveCount(5);
  await expect(page.getByRole("link", { name: "Selbst antreten" })).toHaveAttribute("href", /\/spielen\/sprint$/);
  await expectNoHorizontalOverflow(page);
  const desktopHero = await page.evaluate(() => {
    const intro = document.querySelector(".competition-hero > div:first-child");
    const hero = document.querySelector(".competition-hero");
    if (!intro || !hero) {
      throw new Error("Wettbewerb-Hero fehlt.");
    }

    const introBounds = intro.getBoundingClientRect();
    const after = getComputedStyle(intro, "::after");
    return {
      heroBottom: Math.round(hero.getBoundingClientRect().bottom),
      introHeight: Math.round(introBounds.height),
      pseudoDisplay: after.display,
      pseudoContent: after.content,
      viewportHeight: window.innerHeight
    };
  });
  expect(desktopHero.introHeight, "Wettbewerb-Hero wirkt wie ein leerer Großblock.").toBeLessThanOrEqual(230);
  expect(desktopHero.pseudoDisplay).toBe("none");
  expect(desktopHero.pseudoContent).toBe("none");
  expect(desktopHero.heroBottom).toBeLessThan(desktopHero.viewportHeight * 0.7);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/ranglisten?board=sprint&period=day&mode=sprint60");
  await expectCompactMobileHeader(page);
  await expectResponsiveAppShell(page, 390);
  await expect(page.locator(".competition-tabs a")).toHaveCount(5);
  await expect(page.locator(".competition-layout")).toBeVisible();
  await expectNoHorizontalOverflow(page);
  const mobileTabs = await page.evaluate(() => [...document.querySelectorAll(".competition-tabs a")].map((tab) => {
    const bounds = tab.getBoundingClientRect();
    return {
      text: tab.textContent.trim(),
      left: Math.round(bounds.left),
      right: Math.round(bounds.right),
      viewportWidth: window.innerWidth
    };
  }));
  expect(mobileTabs.map((tab) => tab.text)).toEqual(["Arena", "Sprint", "Texte", "Challenges", "XP"]);
  const mobileTabRail = await page.locator(".competition-tabs").evaluate((tabs) => ({
    clientWidth: tabs.clientWidth,
    scrollWidth: tabs.scrollWidth,
    top: Math.round(tabs.getBoundingClientRect().top),
    bottom: Math.round(tabs.getBoundingClientRect().bottom),
    viewportHeight: window.innerHeight
  }));
  expect(mobileTabs[0].left).toBeGreaterThanOrEqual(0);
  expect(mobileTabRail.scrollWidth).toBeGreaterThanOrEqual(mobileTabRail.clientWidth);
  expect(mobileTabRail.bottom).toBeLessThan(mobileTabRail.viewportHeight - 68);
  const mobileHeroStats = await page.evaluate(() => {
    const stats = document.querySelector(".competition-hero-stats");
    if (!stats) {
      return { display: "missing", visibleText: "" };
    }

    return {
      display: getComputedStyle(stats).display,
      visibleText: stats.textContent.replace(/\s+/g, " ").trim()
    };
  });
  expect(mobileHeroStats.display === "none" || mobileHeroStats.visibleText.includes("Top-Ausschnitt")).toBe(true);

  await page.setViewportSize({ width: 768, height: 1024 });
  await page.goto("/ranglisten?board=sprint&period=day&mode=sprint60");
  await expectResponsiveAppShell(page, 768);
  const tabletCompetition = await page.evaluate(() => {
    const hero = document.querySelector(".competition-hero")?.getBoundingClientRect();
    const board = document.querySelector(".competition-main")?.getBoundingClientRect();
    return {
      heroBottom: Math.round(hero?.bottom ?? 9999),
      boardTop: Math.round(board?.top ?? 9999),
      viewportHeight: window.innerHeight
    };
  });
  expect(tabletCompetition.heroBottom).toBeLessThan(tabletCompetition.viewportHeight * 0.72);
  expect(tabletCompetition.boardTop).toBeLessThan(tabletCompetition.viewportHeight);

  await page.goto("/profil/einstellungen");
  await page.getByLabel("In Ranglisten sichtbar").uncheck();
  await page.getByRole("button", { name: "Speichern" }).click();
  await page.goto("/ranglisten");
  await expect(page.getByText("Du bist aktuell nicht öffentlich in Ranglisten sichtbar.")).toBeVisible();
});

test("Tippabschluss zeigt Motivation ohne bewegte Pflichtanimation", async ({ page }, testInfo) => {
  await login(page, `browser.motivation.${testInfo.workerIndex}`);
  await page.goto("/spielen");
  const input = page.locator("[data-input]");
  await expect(input).toBeEnabled({ timeout: 15_000 });
  const target = (await page.locator("[data-target]").textContent()).trim();
  expect(target.length).toBeGreaterThan(80);

  await input.fill(firstGraphemes(target, 1));
  await page.waitForTimeout(5_200);
  await input.fill(target);
  await expect(page.locator(".motivation-panel")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".finish-panel")).toBeVisible();
  await expect(page.locator(".xp-reveal")).toBeVisible();
  await expect(page.locator(".motivation-event").first()).toBeVisible();
  await expect(page.locator(".xp-chip").first()).toBeVisible();
  const finishedStatus = await page.locator(".play-quickstart .typing-timer").evaluate((timer) =>
    getComputedStyle(timer, "::before").content);
  expect(finishedStatus).toContain("FERTIG");
  await expectNoHorizontalOverflow(page);
});

test("Raumformular blockiert doppelte Submit-Aktion im echten Browser", async ({ page }) => {
  await login(page, "browser.submit.guard");
  await page.goto("/arena/neu");
  await expect(page.getByText("Textvorschau")).toBeVisible();
  await expect(page.getByText("Klassisches Rennen", { exact: true })).toBeVisible();
  await expect(page.getByText("So sieht der Raum aus")).toBeVisible();
  const textSelect = page.locator("[data-arena-text-select]");
  const optionCount = await textSelect.locator("option").count();
  expect(optionCount).toBeGreaterThan(1);
  const previewBefore = (await page.locator("[data-text-preview-body]").textContent())?.trim();
  await textSelect.selectOption({ index: 1 });
  await expect.poll(async () => (await page.locator("[data-text-preview-body]").textContent())?.trim()).not.toBe(previewBefore);
  await page.getByLabel("Titel").fill("Submit Guard Browser");
  await page.getByLabel("Maximale Teilnehmer").fill("2");

  const guardState = await page.evaluate(() => {
    const form = document.querySelector("form[data-submit-guard]");
    const button = form?.querySelector("button[type='submit']");
    if (!form || !button) {
      throw new Error("Submit-Guard-Formular fehlt.");
    }

    let observedSubmits = 0;
    form.addEventListener("submit", (event) => {
      observedSubmits += 1;
      event.preventDefault();
    });

    const first = new Event("submit", { bubbles: true, cancelable: true });
    const second = new Event("submit", { bubbles: true, cancelable: true });
    form.dispatchEvent(first);
    form.dispatchEvent(second);

    return {
      buttonDisabled: button.disabled,
      buttonText: button.textContent.trim(),
      firstPrevented: first.defaultPrevented,
      observedSubmits,
      secondPrevented: second.defaultPrevented,
      submitting: form.dataset.submitting
    };
  });

  expect(guardState).toEqual({
    buttonDisabled: true,
    buttonText: "Raum wird erstellt...",
    firstPrevented: true,
    observedSubmits: 2,
    secondPrevented: true,
    submitting: "true"
  });
});

test("Texttraining zeigt Absatzwechsel als Enter-Stelle", async ({ page }, testInfo) => {
  await login(page, `browser.paragraph.${testInfo.workerIndex}`);

  const title = `Absatz Browser ${testInfo.workerIndex}-${Date.now()}`;
  const firstLine = "Der erste Absatz endet bewusst vor einem neuen Gedanken.";
  const secondLine = "Der zweite Absatz beginnt sichtbar in einer neuen Zeile, damit niemand versehentlich ein Leerzeichen tippt.";
  await page.goto("/texte/neu");
  await page.getByLabel("Titel").fill(title);
  await page.getByLabel("Text").fill(`${firstLine}\n${secondLine}`);
  await page.getByRole("button", { name: "Text speichern" }).click();
  await expect(page.getByRole("heading", { name: title })).toBeVisible();

  await page.getByRole("link", { name: "Trainieren" }).click();
  await expect(page.getByRole("heading", { name: title })).toBeVisible();
  await expect(page.locator("[data-input]")).toBeEnabled();
  const newlineMarker = page.locator("[data-target] .typing-newline");
  await expect(newlineMarker).toHaveText("↵");

  const markerLayout = await page.locator("[data-target]").evaluate((target) => {
    const marker = target.querySelector(".typing-newline");
    const nextSpan = marker?.nextSibling?.nextSibling;
    if (!(marker instanceof HTMLElement) || !(nextSpan instanceof HTMLElement)) {
      throw new Error("Absatzmarker oder Folgezeichen fehlen.");
    }

    return {
      brAfterMarker: marker.nextSibling?.nodeName === "BR",
      nextLineBelow: nextSpan.getBoundingClientRect().top > marker.getBoundingClientRect().top,
      title: marker.getAttribute("title")
    };
  });
  expect(markerLayout).toEqual({
    brAfterMarker: true,
    nextLineBelow: true,
    title: "Absatz: Enter drücken"
  });

  await page.locator("[data-input]").fill(`${firstLine} `);
  await expect(newlineMarker).toHaveClass(/wrong/);
  await page.locator("[data-input]").fill(`${firstLine}\n`);
  await expect(newlineMarker).toHaveClass(/correct/);
});

test("Arena läuft mit zwei getrennten Browserkontexten über SignalR", async ({ browser, baseURL }, testInfo) => {
  const hostContext = await browser.newContext({ baseURL, colorScheme: "dark", reducedMotion: "reduce" });
  const guestContext = await browser.newContext({ baseURL, colorScheme: "dark", reducedMotion: "reduce" });
  await hostContext.grantPermissions(["clipboard-read", "clipboard-write"], { origin: baseURL });
  await hostContext.addInitScript(() => {
    Object.defineProperty(navigator, "share", {
      configurable: true,
      value: undefined
    });
  });
  const suffix = testInfo.workerIndex.toString();
  const hostName = `arena.host.${suffix}`;
  const guestName = `arena.gast.${suffix}`;

  try {
    const host = await hostContext.newPage();
    const guest = await guestContext.newPage();

    await login(host, hostName);
    await login(guest, guestName);

    await host.goto("/arena/neu");
    await host.getByLabel("Titel").fill(`Browser-Arena ${suffix}`);
    await host.getByLabel("Maximale Teilnehmer").fill("2");
    await host.getByRole("button", { name: "Raum erstellen" }).click();
    await expect(host).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
    await expect(host.getByRole("button", { name: "Starten" })).toBeVisible();
    await expect(host.locator("[data-arena-timer] strong")).not.toHaveText("00:00.0");
    await expect(host.locator(".arena-phase-steps li.active")).toHaveText("Lobby");

    const roomUrl = host.url();
    const roomCode = (await host.locator(".room-code strong").textContent()).trim();
    await host.getByRole("button", { name: "Code kopieren" }).click();
    await expect(host.locator("[data-copy-status]")).toHaveText("Raumcode kopiert.");
    await expect.poll(() => host.evaluate(() => navigator.clipboard.readText())).toBe(roomCode);
    await host.getByRole("button", { name: "Einladung teilen" }).click();
    await expect(host.locator("[data-copy-status]")).toHaveText("Raumcode kopiert.");
    await expect.poll(() => host.evaluate(() => navigator.clipboard.readText())).toBe(roomCode);

    await guest.goto("/arena/beitreten");
    await guest.getByLabel("Raumcode").fill(roomCode);
    await guest.getByRole("button", { name: "Beitreten" }).click();
    await expect(guest).toHaveURL(roomUrl);
    await expect(guest.getByRole("button", { name: "Starten" })).toHaveCount(0);
    await expectArenaConnected(host);
    await expectArenaConnected(guest);

    await guest.getByRole("button", { name: "Bereit" }).click();
    await expect(guest.getByRole("button", { name: "Nicht bereit" })).toBeVisible();
    await host.getByRole("button", { name: "Bereit" }).click();
    await expect(host.getByRole("button", { name: "Nicht bereit" })).toBeVisible();

    await host.getByRole("button", { name: "Starten" }).click();
    await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });
    await expect(guest.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });
    await expect(host.locator(".arena-phase-steps li.active")).toHaveText("Rennen", { timeout: 12_000 });
    await expect(host.getByRole("button", { name: "Starten" })).toHaveCount(0);
    await expect(host.getByRole("button", { name: "Nicht bereit" })).toHaveCount(0);

    const hostTarget = (await host.locator("[data-arena-target]").textContent()).trim();
    const guestTarget = (await guest.locator("[data-arena-target]").textContent()).trim();
    await expect(host.locator("[data-arena-input]")).toBeEnabled();
    await expect(guest.locator("[data-arena-input]")).toBeEnabled();
    await expect.poll(async () => (await host.locator("[data-arena-timer] strong").textContent()).trim()).not.toBe("00:00.0");

    await host.setViewportSize({ width: 390, height: 844 });
    await expectResponsiveAppShell(host, 390);
    await expectArenaCoreInFirstView(host);
    await host.setViewportSize({ width: 1366, height: 768 });

    const layoutOrder = await host.evaluate(() => {
      const target = document.querySelector("[data-arena-target]")?.getBoundingClientRect();
      const input = document.querySelector("[data-arena-input]")?.getBoundingClientRect();
      const track = document.querySelector("[data-arena-track]")?.getBoundingClientRect();
      if (!target || !input || !track) {
        throw new Error("Arena-Layout-Elemente fehlen.");
      }

      return {
        inputBeforeTrack: input.top < track.top,
        targetBeforeInput: target.top < input.top,
        targetInUpperViewport: target.top < window.innerHeight * 0.5
      };
    });
    expect(layoutOrder).toEqual({
      inputBeforeTrack: true,
      targetBeforeInput: true,
      targetInUpperViewport: true
    });

    const hostRowOnGuest = guest.locator(".live-typing-row").filter({ hasText: displayName(hostName) });
    const hostPreviewOnGuest = hostRowOnGuest.locator("[data-live-preview]");
    const firstStableInput = firstStableGraphemes(hostTarget, 3);
    await host.locator("[data-arena-input]").fill(firstStableInput);
    await expect(hostPreviewOnGuest.locator(".correct")).toHaveCount(Array.from(firstStableInput).length, { timeout: 12_000 });

    const targetGraphemes = Array.from(hostTarget);
    const wrongInput = `${targetGraphemes.slice(0, 2).join("")}${wrongCharacterFor(targetGraphemes[2])}`;
    await host.locator("[data-arena-input]").fill(wrongInput);
    await expect(hostPreviewOnGuest.locator(".wrong")).toHaveCount(1, { timeout: 12_000 });

    await host.locator("[data-arena-input]").fill(firstGraphemes(hostTarget, 2));
    await expect(hostPreviewOnGuest.locator(".wrong")).toHaveCount(0, { timeout: 12_000 });
    await expect(hostPreviewOnGuest.locator(".correct")).toHaveCount(2, { timeout: 12_000 });

    await host.locator("[data-arena-input]").fill(hostTarget);
    await guest.locator("[data-arena-input]").fill(guestTarget);

    await expect(host.locator("[data-arena-podium]")).toBeVisible({ timeout: 12_000 });
    await expect(guest.locator("[data-arena-podium]")).toBeVisible({ timeout: 12_000 });
    await expect(host.locator("[data-arena-podium]")).toContainText(displayName(hostName));
    await expect(guest.locator("[data-arena-podium]")).toContainText(displayName(guestName));
  } finally {
    await guestContext.close();
    await hostContext.close();
  }
});
