const { test, expect } = require("@playwright/test");
const { writeFile } = require("node:fs/promises");

const DESKTOP_VIEWPORT = { width: 1366, height: 768 };
const MOBILE_VIEWPORT = { width: 390, height: 844 };
const FUNCTIONAL_CONVERGENCE_TIMEOUT_MS = 45_000;
const PARTICIPANTS = [
  { username: "arena.team.host", context: "host" },
  { username: "arena.team.gast.eins", context: "guest-1" },
  { username: "arena.team.gast.zwei", context: "guest-2" },
  { username: "arena.team.gast.drei", context: "guest-3" }
];

function displayName(username) {
  return username
    .replace(/[._]/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1).toLowerCase()}`)
    .join(" ");
}

function firstStableGraphemes(value, count) {
  const graphemes = Array.from(value);
  let length = Math.min(count, graphemes.length - 1);
  while (length < graphemes.length - 1 && /\s/u.test(graphemes[length - 1])) {
    length += 1;
  }

  return graphemes.slice(0, length).join("");
}

function collectBrowserErrors(page, context, browserErrors) {
  page.on("pageerror", (error) => {
    browserErrors.push({ context, kind: "pageerror", message: error.message });
  });
  page.on("console", (message) => {
    if (message.type() !== "error") {
      return;
    }

    browserErrors.push({
      context,
      kind: "console",
      message: message.text(),
      location: message.location()
    });
  });
}

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible({ timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
  await expect(page.locator(".sidebar-profile")).toContainText(displayName(username));
}

async function expectArenaConnected(page) {
  await expect(page.locator("[data-arena-connection-quality]"))
    .toHaveText(/Verbindung: (aktiv|neu verbunden)/, { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
  await expect(page.getByText("Arena-Verbindung ist nicht aktiv.")).toHaveCount(0);
}

async function readRoster(page) {
  return page.locator("[data-arena-participants] tr").evaluateAll((rows) => rows
    .map((row) => {
      const cells = [...row.querySelectorAll("td")];
      return {
        name: cells[0]?.textContent?.trim() || "",
        team: cells[1]?.textContent?.trim() || "",
        status: cells[2]?.textContent?.trim() || "",
        progress: cells[3]?.textContent?.trim() || "",
        points: cells[4]?.textContent?.trim() || "",
        placement: cells[5]?.textContent?.trim() || ""
      };
    })
    .sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0));
}

async function waitForRosterConvergence(pages, expectedNames) {
  const sortedNames = [...expectedNames].sort();
  const expectedRosters = Array.from({ length: pages.length }, () => sortedNames);
  await expect.poll(async () => Promise.all(pages.map(async (page) =>
    (await readRoster(page)).map((participant) => participant.name))), {
    timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS,
    message: "Alle vier Browserkontexte sehen denselben vollständigen Arena-Kader."
  }).toEqual(expectedRosters);
}

async function measurePhaseFanOut(pages, contexts, stateText, activeStep, startedAt) {
  const recipients = await Promise.all(pages.map(async (page, index) => {
    await expect(page.locator("[data-arena-state]"))
      .toHaveText(stateText, { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    await expect(page.locator(".arena-phase-steps li.active"))
      .toHaveText(activeStep, { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    return { context: contexts[index], latencyMs: Date.now() - startedAt };
  }));

  return {
    recipients,
    maxMs: Math.max(...recipients.map((item) => item.latencyMs))
  };
}

async function measureProgressFanOut(pages, contexts, sourceName, expectedCorrectCharacters, startedAt) {
  const recipients = await Promise.all(pages.map(async (page, index) => {
    const sourcePreview = page.locator(".live-typing-row")
      .filter({ hasText: sourceName })
      .locator("[data-live-preview]");
    await expect(sourcePreview.locator(".correct"))
      .toHaveCount(expectedCorrectCharacters, { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    return { context: contexts[index], latencyMs: Date.now() - startedAt };
  }));

  return {
    recipients,
    maxMs: Math.max(...recipients.map((item) => item.latencyMs))
  };
}

async function readTeamStandings(page) {
  return page.locator("[data-arena-teams] [data-team-number]").evaluateAll((rows) => rows
    .map((row) => {
      const title = row.querySelector("strong")?.textContent?.trim() || "";
      const placementMatch = title.match(/^(\d+)\.\s+(.+)$/u);
      const detail = row.querySelector("span")?.textContent?.trim() || "";
      const pointsMatch = detail.match(/^(\d+) Punkte/u);
      return {
        teamNumber: Number(row.dataset.teamNumber),
        placement: placementMatch ? Number(placementMatch[1]) : null,
        name: placementMatch ? placementMatch[2] : title,
        points: pointsMatch ? Number(pointsMatch[1]) : null,
        detail
      };
    })
    .sort((left, right) => left.teamNumber - right.teamNumber));
}

async function expectNoHorizontalOverflow(page) {
  const overflow = await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const offenders = [...document.body.querySelectorAll("*")]
      .filter((element) => element instanceof HTMLElement)
      .filter((element) => element.getBoundingClientRect().right > viewportWidth + 1)
      .slice(0, 5)
      .map((element) => element.className || element.tagName);
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth,
      offenders
    };
  });

  expect(
    overflow.documentWidth,
    `Mobile Überbreite durch: ${overflow.offenders.join(", ") || "unbekannt"}`
  ).toBeLessThanOrEqual(overflow.viewportWidth + 1);
}

async function attachScreenshot(page, testInfo, name) {
  const path = testInfo.outputPath(`${name}.png`);
  await page.screenshot({ path, fullPage: false, animations: "disabled" });
  await testInfo.attach(name, { path, contentType: "image/png" });
}

test("Vier isolierte Browserkontexte absolvieren ein echtes Teamrennen über SignalR", async ({ browser, baseURL }, testInfo) => {
  testInfo.setTimeout(240_000);
  const contexts = [];
  const pages = [];
  const browserErrors = [];
  const contextNames = PARTICIPANTS.map((participant) => participant.context);
  const expectedNames = PARTICIPANTS.map((participant) => displayName(participant.username));

  try {
    for (const participant of PARTICIPANTS) {
      const context = await browser.newContext({
        baseURL,
        colorScheme: "dark",
        reducedMotion: "reduce",
        viewport: DESKTOP_VIEWPORT
      });
      contexts.push(context);
      const page = await context.newPage();
      collectBrowserErrors(page, participant.context, browserErrors);
      pages.push(page);
      await login(page, participant.username);
    }

    const [host, ...guests] = pages;
    await host.goto("/arena/neu");
    await host.locator('[data-arena-mode-input][value="Team"]').check();
    await host.getByLabel("Titel").fill("Browser-Teamrennen mit vier Kontexten");
    await host.getByLabel("Maximale Teilnehmer").fill("4");
    await host.getByRole("button", { name: "Raum erstellen" }).click();
    await expect(host).toHaveURL(/\/arena\/[0-9a-f-]{36}$/i);
    await expectArenaConnected(host);

    const roomUrl = host.url();
    const roomCode = (await host.locator(".room-code strong").textContent()).trim();
    const lobbyStartedAt = Date.now();
    for (const guest of guests) {
      await guest.goto("/arena/beitreten");
      await guest.getByLabel("Raumcode").fill(roomCode);
      await guest.getByRole("button", { name: "Beitreten" }).click();
      await expect(guest).toHaveURL(roomUrl);
      await expectArenaConnected(guest);
    }

    await waitForRosterConvergence(pages, expectedNames);
    await Promise.all(pages.map(async (page) => {
      await expect(page.locator("[data-arena-state]")).toHaveText("Lobby");
      await expect(page.locator(".arena-phase-steps li.active")).toHaveText("Lobby");
      await expect(page.locator("[data-arena-participant-count]"))
        .toHaveText("4 Personen", { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    }));
    const lobbyConvergenceMs = Date.now() - lobbyStartedAt;

    const lobbyRosters = await Promise.all(pages.map(readRoster));
    const canonicalLobbyRoster = lobbyRosters[0];
    expect(canonicalLobbyRoster).toHaveLength(4);
    expect(new Set(canonicalLobbyRoster.map((participant) => participant.name)).size).toBe(4);
    expect(canonicalLobbyRoster.map((participant) => participant.name)).toEqual([...expectedNames].sort());
    for (const roster of lobbyRosters) {
      expect(roster).toEqual(canonicalLobbyRoster);
    }

    const teamCounts = canonicalLobbyRoster.reduce((counts, participant) => {
      counts[participant.team] = (counts[participant.team] || 0) + 1;
      return counts;
    }, {});
    expect(teamCounts).toEqual({ Alpha: 2, Bravo: 2 });
    await attachScreenshot(host, testInfo, "arena-team-lobby-desktop");

    for (let index = 0; index < pages.length; index += 1) {
      const readyPage = pages[index];
      await readyPage.getByRole("button", { name: "Bereit", exact: true }).click();
      await Promise.all(pages.map((observer) => {
        const participantStatus = observer.locator("[data-arena-participants] tr")
          .filter({ hasText: expectedNames[index] })
          .locator('td[data-label="Status"]');
        return expect(participantStatus)
          .toHaveText("Bereit", { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
      }));
      await expect(readyPage.getByRole("button", { name: "Nicht bereit", exact: true }))
        .toBeVisible({ timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    }
    const startButton = host.getByRole("button", { name: "Starten", exact: true });
    await expect(startButton).toBeEnabled({ timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    const startFanOutStartedAt = Date.now();
    await startButton.click();
    const runningFanOut = await measurePhaseFanOut(
      pages,
      contextNames,
      "Rennen läuft",
      "Rennen",
      startFanOutStartedAt
    );

    await Promise.all(pages.map((page) =>
      expect(page.locator("[data-arena-input]"))
        .toBeEnabled({ timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS })));
    const targetTexts = await Promise.all(pages.map(async (page) =>
      ((await page.locator("[data-arena-target]").textContent()) || "").trim()));
    expect(new Set(targetTexts).size).toBe(1);
    expect(Array.from(targetTexts[0]).length).toBeGreaterThan(100);

    const progressInput = firstStableGraphemes(targetTexts[0], 24);
    const progressCharacters = Array.from(progressInput).length;
    expect(progressCharacters).toBeGreaterThan(0);
    const progressFanOutStartedAt = Date.now();
    await host.locator("[data-arena-input]").fill(progressInput);
    const progressFanOut = await measureProgressFanOut(
      pages,
      contextNames,
      expectedNames[0],
      progressCharacters,
      progressFanOutStartedAt
    );
    await attachScreenshot(host, testInfo, "arena-team-progress-desktop");

    for (let index = 0; index < pages.length - 1; index += 1) {
      await pages[index].locator("[data-arena-input]").fill(targetTexts[index]);
      const status = host.locator("[data-arena-participants] tr")
        .filter({ hasText: expectedNames[index] })
        .locator('td[data-label="Status"]');
      await expect(status).toHaveText("Fertig", { timeout: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS });
    }

    const resultFanOutStartedAt = Date.now();
    await pages.at(-1).locator("[data-arena-input]").fill(targetTexts.at(-1));
    const resultFanOut = await measurePhaseFanOut(
      pages,
      contextNames,
      "Ergebnisse",
      "Ergebnis",
      resultFanOutStartedAt
    );

    const resultRosters = await Promise.all(pages.map(readRoster));
    const canonicalResultRoster = resultRosters[0];
    for (const roster of resultRosters) {
      expect(roster).toEqual(canonicalResultRoster);
    }
    expect(canonicalResultRoster).toHaveLength(4);
    expect(canonicalResultRoster.every((participant) => participant.status === "Fertig")).toBe(true);

    const targetCharacterCount = Array.from(targetTexts[0]).length;
    expect(canonicalResultRoster.every((participant) =>
      participant.progress === `${targetCharacterCount} / ${targetCharacterCount}`)).toBe(true);

    const teamStandings = await Promise.all(pages.map(readTeamStandings));
    const canonicalTeamStanding = teamStandings[0];
    expect(canonicalTeamStanding).toHaveLength(2);
    expect(canonicalTeamStanding.map((team) => team.name)).toEqual(["Team Alpha", "Team Bravo"]);
    expect(canonicalTeamStanding.map((team) => team.placement).sort()).toEqual([1, 2]);
    expect(canonicalTeamStanding.every((team) => Number.isInteger(team.points) && team.points > 0)).toBe(true);
    for (const standing of teamStandings) {
      expect(standing).toEqual(canonicalTeamStanding);
    }

    const mobileResult = pages.at(-1);
    await mobileResult.setViewportSize(MOBILE_VIEWPORT);
    await expect(mobileResult.locator("[data-arena-team-board]")).toBeVisible();
    await mobileResult.locator("[data-arena-team-board]").scrollIntoViewIfNeeded();
    expect(await mobileResult.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight })))
      .toEqual(MOBILE_VIEWPORT);
    await expectNoHorizontalOverflow(mobileResult);
    await attachScreenshot(mobileResult, testInfo, "arena-team-result-mobile-390x844");

    const latencyMetric = {
      generatedAt: new Date().toISOString(),
      participants: pages.length,
      functionalConvergenceTimeoutMs: FUNCTIONAL_CONVERGENCE_TIMEOUT_MS,
      lobbyConvergenceMs,
      runningFanOut,
      progressFanOut,
      resultFanOut,
      raceTotalMs: Date.now() - startFanOutStartedAt
    };
    const latencyPath = testInfo.outputPath("arena-team-latency.json");
    await writeFile(latencyPath, `${JSON.stringify(latencyMetric, null, 2)}\n`, "utf8");
    await testInfo.attach("arena-team-latency.json", {
      path: latencyPath,
      contentType: "application/json"
    });
    console.log(`[arena-team-multiplayer] Gemessene Dauern: ${JSON.stringify(latencyMetric)}`);

    await pages[0].waitForTimeout(250);
    expect(browserErrors, "Page- oder Console-Fehler in einem der vier Browserkontexte").toEqual([]);
  } finally {
    await Promise.allSettled(contexts.reverse().map((context) => context.close()));
  }
});
