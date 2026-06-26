const { test, expect } = require("@playwright/test");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.getByRole("heading", { name: new RegExp(`Hallo, ${displayName(username)}`) })).toBeVisible();
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

  expect(overflow.documentWidth, `Overflow durch: ${overflow.offenders.join(", ")}`).toBeLessThanOrEqual(overflow.viewportWidth + 1);
}

test("Dashboard und Einstellungen rendern im echten Browser", async ({ page }) => {
  await login(page, "browser.smoke");

  await expect(page.getByText("Tagesfokus")).toBeVisible();
  await expect(page.locator(".level-cockpit")).toBeVisible();
  await expect(page.locator(".quest-card").first()).toBeVisible();
  await expect(page.locator(".event-feed")).toBeVisible();
  await expectNoHorizontalOverflow(page);
  await expect(page.getByText("30-Tage-Übersicht")).toBeVisible();
  await page.goto("/profil/einstellungen");
  await expect(page.getByRole("heading", { name: "Einstellungen" })).toBeVisible();
  await expect(page.getByText("Identität aus AD/LDAP")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "de");
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
  await expect(page.locator(".filter-panel")).toBeVisible();
  await expect(page.locator(".text-card").first()).toBeVisible();
  await expectNoHorizontalOverflow(page);
});

test("Spielseite zeigt Sofortrunde und Modi sauber auf Desktop und Mobile", async ({ page }) => {
  await login(page, "browser.play.ui");
  await page.goto("/spielen");
  await expect(page.locator(".play-quickstart")).toBeVisible();
  await expect(page.locator(".play-mode-link")).toHaveCount(3);
  await expect(page.locator(".play-text-card").first()).toBeVisible();
  await expectNoHorizontalOverflow(page);

  const desktopPosition = await page.locator(".play-quickstart").evaluate((quickstart) => {
    const bounds = quickstart.getBoundingClientRect();
    return { top: Math.round(bounds.top), viewportHeight: window.innerHeight };
  });
  expect(desktopPosition.top).toBeLessThan(desktopPosition.viewportHeight);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/spielen");
  await expect(page.locator(".play-quickstart")).toBeVisible();
  await expect(page.locator(".play-mode-link")).toHaveCount(3);
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
  await expect(page.locator(".motivation-event").first()).toBeVisible();
  await expect(page.locator(".xp-chip").first()).toBeVisible();
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

    await guest.getByRole("button", { name: "Bereit" }).click();
    await expect(guest.getByRole("button", { name: "Nicht bereit" })).toBeVisible();
    await host.getByRole("button", { name: "Bereit" }).click();
    await expect(host.getByRole("button", { name: "Nicht bereit" })).toBeVisible();

    await host.getByRole("button", { name: "Starten" }).click();
    await expect(host.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });
    await expect(guest.locator("[data-arena-state]")).toHaveText("Rennen läuft", { timeout: 12_000 });

    const hostTarget = (await host.locator("[data-arena-target]").textContent()).trim();
    const guestTarget = (await guest.locator("[data-arena-target]").textContent()).trim();
    await expect(host.locator("[data-arena-input]")).toBeEnabled();
    await expect(guest.locator("[data-arena-input]")).toBeEnabled();
    await expect.poll(async () => (await host.locator("[data-arena-timer] strong").textContent()).trim()).not.toBe("00:00.0");

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
