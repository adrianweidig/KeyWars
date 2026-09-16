const { test, expect } = require("@playwright/test");
const { fakeSignalRSource } = require("./arena-test-helpers");

async function login(page, username) {
  await page.goto("/anmelden");
  await page.getByLabel("Benutzername").fill(username);
  await page.getByLabel("Passwort").fill("lokales-test-passwort");
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page.locator(".status-cockpit")).toBeVisible();
}

test("Arena aktualisiert 2800 Zielgrapheme ohne Node-Replacement", async ({ page }) => {
  test.setTimeout(90_000);
  await login(page, `browser.arena.dom.${Date.now().toString(36)}`);
  await page.goto("/arena/neu");
  await page.getByLabel("Titel").fill("DOM-Hotpath");
  await page.getByLabel("Maximale Teilnehmer").fill("2");
  await page.getByRole("button", { name: "Raum erstellen" }).click();
  const roomUrl = page.url();

  await page.route("**/vendor/signalr/signalr.min.js*", (route) => route.fulfill({
    status: 200,
    contentType: "application/javascript",
    body: fakeSignalRSource()
  }));
  await page.goto(`${roomUrl}?arenaTest=running&domHotpath=1`);
  const root = page.locator("[data-arena-room]");
  await expect(root).toHaveAttribute("data-connection-state", "connected");

  const targetText = await page.evaluate(() => {
    const seed = "Alpha\nBeta Gamma Delta – klare Umlaute äöü. ";
    const text = Array.from(seed.repeat(100)).slice(0, 2800).join("");
    const connection = window.__arenaFakeConnection;
    const snapshot = connection.snapshot();
    snapshot.stateVersion = 2800;
    snapshot.targetText = text;
    snapshot.targetCharacterCount = Array.from(text).length;
    connection.emit("roomChanged", snapshot);
    return text;
  });
  await expect(page.locator("[data-arena-target] > span")).toHaveCount(2800);

  const initial = await page.locator("[data-arena-target]").evaluate((target) => {
    window.__arenaTargetMutations = { childList: 0, classAttributes: 0 };
    window.__arenaTargetObserver = new MutationObserver((records) => {
      for (const record of records) {
        if (record.type === "childList") {
          window.__arenaTargetMutations.childList += 1;
        } else if (record.type === "attributes" && record.attributeName === "class") {
          window.__arenaTargetMutations.classAttributes += 1;
        }
      }
    });
    window.__arenaTargetObserver.observe(target, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["class"]
    });
    return {
      childNodes: target.childNodes.length,
      spans: target.querySelectorAll(":scope > span").length,
      newlineBreaks: target.querySelectorAll(":scope > br").length
    };
  });

  const input = page.locator("[data-arena-input]");
  await input.evaluate((element, text) => {
    const graphemes = Array.from(text);
    const update = (value, inputType, data = null) => {
      element.value = value;
      element.dispatchEvent(new InputEvent("input", {
        bubbles: true,
        data,
        inputType
      }));
    };

    for (let length = 1; length <= 100; length += 1) {
      update(graphemes.slice(0, length).join(""), "insertText", graphemes[length - 1]);
    }

    const prefix = graphemes.slice(0, 99).join("");
    update(prefix, "deleteContentBackward");
    update(`${prefix}#`, "insertText", "#");
    update(graphemes.slice(0, 100).join(""), "insertText", graphemes[99]);
  }, targetText);

  await expect(page.locator("[data-arena-target] > span.correct")).toHaveCount(100);
  await expect(page.locator("[data-arena-target] > span.current")).toHaveCount(1);
  const expectedNewlines = Array.from(targetText).slice(0, 100).filter((value) => value === "\n").length;
  await expect(page.locator("[data-arena-target] > span.typing-newline.correct")).toHaveCount(expectedNewlines);
  const result = await page.locator("[data-arena-target]").evaluate((target) => ({
    mutations: window.__arenaTargetMutations,
    childNodes: target.childNodes.length,
    spans: target.querySelectorAll(":scope > span").length,
    newlineBreaks: target.querySelectorAll(":scope > br").length,
    horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1
  }));
  expect(result.childNodes).toBe(initial.childNodes);
  expect(result.spans).toBe(initial.spans);
  expect(result.newlineBreaks).toBe(initial.newlineBreaks);
  expect(result.mutations.childList).toBe(0);
  expect(result.mutations.classAttributes).toBeGreaterThan(0);
  expect(result.horizontalOverflow).toBe(false);
});
