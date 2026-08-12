const { test, expect } = require("@playwright/test");
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

test("Arena verwirft veraltete Zustände, dekodiert kompakte Deltas und startet nach Reconnect-Erschöpfung neu", async ({ page }, testInfo) => {
  await login(page, `browser.arena.realtime.${testInfo.workerIndex}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("Realtime Browser");
  await page.getByLabel("Maximale Teilnehmer").fill("32");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  await expect(page).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
  const roomUrl = page.url();

  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));
  await page.goto(`${roomUrl}?arenaTest=running`);

  const root = page.locator("[data-arena-room]");
  await expect(root).toHaveAttribute("data-connection-state", "connected");
  const profileId = await root.getAttribute("data-current-profile-id");
  const participantRow = page.locator(`[data-arena-participants] [data-participant-id="${profileId}"]`);

  await page.evaluate(() => {
    const fresh = window.__arenaFakeConnection.snapshot();
    fresh.stateVersion = 5;
    fresh.participants[0].sequence = 10;
    fresh.participants[0].correctCharacters = 10;
    fresh.participants[0].typedTextPreview = "c".repeat(10);
    window.__arenaFakeConnection.emit("roomChanged", fresh);

    const stale = structuredClone(fresh);
    stale.stateVersion = 4;
    stale.participants[0].sequence = 9;
    stale.participants[0].correctCharacters = 1;
    stale.participants[0].typedTextPreview = "c";
    window.__arenaFakeConnection.emit("roomChanged", stale);
  });
  await expect(participantRow.locator("td").nth(3)).toContainText("10 /");

  await page.evaluate(() => {
    const connection = window.__arenaFakeConnection;
    const snapshot = connection.snapshot();
    const participantId = snapshot.participants[0].profileId;
    connection.emit("progressChanged", {
      roomId: snapshot.roomId,
      roomVersion: 1,
      serverNow: new Date().toISOString(),
      deltas: [{
        roomId: snapshot.roomId,
        roomVersion: 1,
        stateVersion: 6,
        participantId,
        participantSequence: 11,
        correctCharacters: 2,
        typedCharacters: 4,
        typedStateBits: "Cw==",
        wpm: 44,
        accuracy: 75,
        rankHint: 1
      }]
    });
    connection.emit("progressChanged", {
      roomId: snapshot.roomId,
      roomVersion: 1,
      serverNow: new Date().toISOString(),
      deltas: [{
        roomId: snapshot.roomId,
        roomVersion: 1,
        stateVersion: 7,
        participantId,
        participantSequence: 10,
        correctCharacters: 0,
        typedCharacters: 1,
        typedStateBits: "AA==",
        wpm: 0,
        accuracy: 0,
        rankHint: 1
      }]
    });
  });

  await expect(participantRow.locator("td").nth(3)).toContainText("2 /");
  const livePreview = page.locator(`[data-live-participant-id="${profileId}"] [data-live-preview]`);
  await expect(livePreview.locator(".correct")).toHaveCount(3);
  await expect(livePreview.locator(".wrong")).toHaveCount(1);
  await expect(page.locator(".arena-phase-steps li.active")).toHaveAttribute("aria-current", "step");

  await page.evaluate(() => {
    const connection = window.__arenaFakeConnection;
    const next = connection.snapshot();
    next.stateVersion = 8;
    next.maxParticipants = 32;
    const current = next.participants[0];
    next.participants = Array.from({ length: 30 }, (_, index) => ({
      ...current,
      profileId: index === 0 ? current.profileId : `00000000-0000-0000-0000-${String(index).padStart(12, "0")}`,
      displayName: `Person ${String(index + 1).padStart(2, "0")}`,
      sequence: index === 0 ? 11 : 0,
      correctCharacters: 30 - index,
      typedTextPreview: ""
    }));
    connection.emit("roomChanged", next);
  });
  await expect(page.locator("[data-arena-participants] [data-participant-id]")).toHaveCount(3);
  await root.evaluate((element) => {
    element.dataset.arenaRosterExpanded = "true";
    element.dispatchEvent(new CustomEvent("keywars:arena-roster-display-change"));
  });
  await expect(page.locator("[data-arena-participants] [data-participant-id]")).toHaveCount(30);

  await page.evaluate(() => window.__arenaFakeConnection.emitClosed("Test-Netzfehler"));
  await expect(root).toHaveAttribute("data-connection-state", "disconnected");
  await expect(page.locator("[data-arena-error]")).toContainText("Test-Netzfehler");
  await expect(root).toHaveAttribute("data-connection-state", "connected", { timeout: 6000 });
  await expect(page.locator("[data-arena-error]")).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => window.__arenaFakeConnection.startAttempts)).toBeGreaterThanOrEqual(2);
  await expect.poll(() => page.evaluate(() => window.__arenaFakeConnection.invocations
    .filter((invocation) => invocation.target === "JoinRoom").length)).toBeGreaterThanOrEqual(2);
});
