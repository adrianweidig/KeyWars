const { test, expect } = require("@playwright/test");
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

async function expectNoHorizontalOverflow(page) {
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
}

test("64er-Arena rendert Fokus, Vollansicht und Mobilansicht begrenzt", async ({ page }, testInfo) => {
  testInfo.setTimeout(120_000);
  await login(page, `browser.arena.large.${Date.now().toString(36)}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("64er-Renderprüfung");
  await page.getByLabel("Maximale Teilnehmer").fill("64");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  await expect(page).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
  const roomUrl = page.url();

  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));
  await page.goto(`${roomUrl}?arenaTest=running&largeRoom=64`);
  const root = page.locator("[data-arena-room]");
  await expect(root).toHaveAttribute("data-connection-state", "connected");

  await page.evaluate(() => {
    const connection = window.__arenaFakeConnection;
    const snapshot = connection.snapshot();
    const current = snapshot.participants[0];
    snapshot.stateVersion = 64;
    snapshot.maxParticipants = 64;
    snapshot.participants = Array.from({ length: 64 }, (_, index) => ({
      ...current,
      profileId: index === 0 ? current.profileId : `00000000-0000-0000-0000-${String(index).padStart(12, "0")}`,
      displayName: index === 0 ? current.displayName : `Person ${String(index + 1).padStart(2, "0")}`,
      sequence: index === 0 ? 1 : 0,
      correctCharacters: 64 - index,
      typedTextPreview: ""
    }));
    connection.emit("roomChanged", snapshot);
  });

  await expect(root).toHaveAttribute("data-arena-display-mode", "focused");
  await expect(page.locator("[data-arena-participant-count]")).toHaveText("64 Personen");
  await expect(page.locator("[data-arena-participants] tr[data-participant-id]")).toHaveCount(3);
  await expect(page.locator("[data-arena-hidden-count]")).toContainText("61 weitere");
  await expectNoHorizontalOverflow(page);

  const expand = page.locator("[data-arena-roster-expand]");
  await expand.click();
  await expect(expand).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("[data-arena-participants] tr[data-participant-id]")).toHaveCount(64);
  await expect(page.locator("[data-arena-track] [data-track-participant-id]")).toHaveCount(64);
  await expect(page.locator("[data-arena-live-board] [data-live-participant-id]")).toHaveCount(64);
  await expect(page.locator("[data-arena-live-board]")).toHaveAttribute("tabindex", "0");
  await expect(page.locator("[data-arena-track]")).toHaveAttribute("tabindex", "0");
  await expect(page.locator(".arena-roster-table-wrap")).toHaveAttribute("tabindex", "0");
  await expectNoHorizontalOverflow(page);

  const expandedLayout = await page.evaluate(() => {
    const liveBoard = document.querySelector("[data-arena-live-board]");
    const raceTrack = document.querySelector("[data-arena-track]");
    const roster = document.querySelector(".arena-roster-table-wrap");
    const viewportHeight = window.innerHeight;
    return {
      liveBoardHeight: Math.round(liveBoard?.getBoundingClientRect().height || 0),
      raceTrackHeight: Math.round(raceTrack?.getBoundingClientRect().height || 0),
      rosterHeight: Math.round(roster?.getBoundingClientRect().height || 0),
      viewportHeight
    };
  });
  expect(expandedLayout.liveBoardHeight).toBeLessThanOrEqual(expandedLayout.viewportHeight);
  expect(expandedLayout.raceTrackHeight).toBeLessThanOrEqual(expandedLayout.viewportHeight);
  expect(expandedLayout.rosterHeight).toBeLessThanOrEqual(expandedLayout.viewportHeight);
  for (const selector of ["[data-arena-live-board]", "[data-arena-track]", ".arena-roster-table-wrap"]) {
    const region = page.locator(selector);
    await region.focus();
    await page.keyboard.press("PageDown");
    await expect.poll(() => region.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  }

  await page.evaluate(() => window.scrollTo(0, 0));
  const desktopScreenshot = testInfo.outputPath("arena-large-room-desktop-1366x768.png");
  await page.screenshot({ path: desktopScreenshot, fullPage: false, animations: "disabled" });
  await testInfo.attach("64er-Arena Desktop", { path: desktopScreenshot, contentType: "image/png" });
  const desktopBoardScreenshot = testInfo.outputPath("arena-large-room-board-desktop.png");
  await page.locator("[data-arena-live-board]").screenshot({ path: desktopBoardScreenshot, animations: "disabled" });
  await testInfo.attach("64er-Liveboard Desktop", { path: desktopBoardScreenshot, contentType: "image/png" });

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(expand).toBeVisible();
  await expand.click();
  await expect(expand).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("[data-arena-participants] tr[data-participant-id]")).toHaveCount(3);
  await expectNoHorizontalOverflow(page);
  await expect(page.locator(".arena-page-header .room-code-share")).toBeVisible();
  await expect(page.locator(".arena-phase-steps [aria-current='step']")).toHaveCount(1);
  await page.evaluate(() => window.scrollTo(0, 0));
  const mobileScreenshot = testInfo.outputPath("arena-large-room-mobile-390x844.png");
  await page.screenshot({ path: mobileScreenshot, fullPage: false, animations: "disabled" });
  await testInfo.attach("64er-Arena Mobil", { path: mobileScreenshot, contentType: "image/png" });

  await page.setViewportSize({ width: 320, height: 568 });
  await expectNoHorizontalOverflow(page);
});
