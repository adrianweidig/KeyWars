import { resetTypingScroll, scrollCurrentCharacterIntoView } from "./typing-scroll.js";

export function attachTypingApps() {
  document.querySelectorAll("[data-typing-app]").forEach((root) => {
    const target = root.querySelector("[data-target]");
    const input = root.querySelector("[data-input]");
    const startButton = root.querySelector("[data-start]");
    const result = root.querySelector("[data-result]");
    const roundStats = root.querySelector("[data-round-stats]");
    const roundStatsContext = root.querySelector("[data-round-stats-context]");
    const challengeId = root.dataset.challengeId || "";
    const autoPrepare = root.dataset.autoPrepare !== "false";
    const initialStartLabel = root.dataset.startLabel || startButton.textContent.trim() || "Starten";
    const preparedStartLabel = root.dataset.preparedLabel || (challengeId ? "Bereit" : "Neue Runde");
    const finishedStartLabel = root.dataset.finishedLabel || (challengeId ? "Abgeschlossen" : preparedStartLabel);
    const idleMessage = root.dataset.idleMessage || "Runde bereit. Starte, sobald du tippen willst.";
    const hideStartWhenPrepared = root.dataset.hideStartWhenPrepared === "true";
    const timer = document.createElement("div");
    const timerValue = document.createElement("strong");
    const timerLabel = document.createElement("span");
    const analysis = document.createElement("div");
    timer.className = "typing-timer";
    timerValue.textContent = "Bereit";
    timerLabel.textContent = "Start bei Eingabe";
    timer.append(timerValue, timerLabel);
    analysis.className = "typing-analysis";
    result.classList.add("typing-result");
    if (root.dataset.timerPlacement === "before-target") {
      target.insertAdjacentElement("beforebegin", timer);
    } else {
      target.insertAdjacentElement("afterend", timer);
    }

    let session = null;
    let startedAt = null;
    let deadlineAt = null;
    let timerFrame = 0;
    let backspaces = 0;
    let focusLosses = 0;
    let finishing = false;
    let finished = false;
    let prepared = false;
    let serverStarted = false;
    let beginPromise = null;
    let lastCompletedWordCount = 0;
    let lastWordBoundaryAt = null;
    const wordDurationsMilliseconds = [];
    const mistakeMap = new Map();
    const numberFormat = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 1 });

    const request = () => ({
      mode: root.dataset.mode || "Sprint60",
      trainingTextId: root.dataset.textId || null,
      sprintSeconds: Number(root.dataset.seconds || sprintSecondsFromMode(root.dataset.mode) || "0"),
      wordCount: Number(root.dataset.words || "80")
    });

    const timedSeconds = () => request().sprintSeconds;
    const isTimed = () => timedSeconds() > 0 && (request().mode || "").startsWith("Sprint");

    const targetWasCompleted = (data) => data.targetCompleted === true ||
      (data.targetCompleted == null && !isTimed() && data.completed === true);

    const updateRoundStats = (data) => {
      if (!roundStats) {
        return;
      }

      const values = {
        wpm: numberFormat.format(data.wpm),
        accuracy: `${numberFormat.format(data.accuracy)} %`,
        correct: numberFormat.format(data.correctCharacters),
        incorrect: numberFormat.format(data.incorrectCharacters),
        consistency: Number(data.consistencySampleCount) >= 2
          ? `${numberFormat.format(data.consistency)} %`
          : "–"
      };
      Object.entries(values).forEach(([name, value]) => {
        const element = roundStats.querySelector(`[data-round-stat="${name}"]`);
        if (element) {
          element.textContent = value;
        }
      });
      roundStats.setAttribute("aria-label", "Ergebnis dieser Runde");
      if (roundStatsContext) {
        roundStatsContext.textContent = "Ergebnis dieser Runde";
      }
    };

    const render = () => {
      if (!session) {
        target.textContent = root.classList.contains("typing-idle") ? idleMessage : "Runde wird vorbereitet.";
        return;
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(session.text);
      renderTypingCharacters(target, expected, (char, index) => {
        if (index < typed.length) {
          return typed[index] === char ? "correct" : "wrong";
        }

        return index === typed.length ? "current" : "";
      });
      scrollCurrentCharacterIntoView(target);
    };

    const formatDuration = (milliseconds) => {
      const value = Math.max(0, milliseconds);
      const minutes = Math.floor(value / 60000);
      const seconds = Math.floor((value % 60000) / 1000);
      const tenths = Math.floor((value % 1000) / 100);
      return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${tenths}`;
    };

    const updateTimer = () => {
      if (startedAt === null || finished) {
        return;
      }

      const now = performance.now();
      const elapsed = now - startedAt;
      if (isTimed()) {
        const remaining = deadlineAt === null ? (timedSeconds() * 1000) - elapsed : deadlineAt - now;
        timerValue.textContent = formatDuration(remaining);
        timerLabel.textContent = "verbleibend";
        if (remaining <= 0) {
          finish();
          return;
        }
      } else {
        timerValue.textContent = formatDuration(elapsed);
        timerLabel.textContent = "vergangen";
      }

      timerFrame = requestAnimationFrame(updateTimer);
    };

    const startTimer = () => {
      if (startedAt !== null || !session || finished) {
        return;
      }

      startedAt = performance.now();
      deadlineAt = isTimed() ? startedAt + (timedSeconds() * 1000) : null;
      lastWordBoundaryAt = startedAt;
      root.classList.remove("typing-prepared");
      root.classList.add("typing-running");
      timerLabel.textContent = isTimed() ? "verbleibend" : "vergangen";
      timerFrame = requestAnimationFrame(updateTimer);
    };

    const resetTimer = () => {
      cancelAnimationFrame(timerFrame);
      startedAt = null;
      deadlineAt = null;
      timerValue.textContent = "Bereit";
      timerLabel.textContent = isTimed() ? `${timedSeconds()} s ab Eingabe` : "Start bei Eingabe";
    };

    const beginAttempt = async () => {
      if (!session || serverStarted) {
        return true;
      }

      if (!beginPromise) {
        beginPromise = fetch("/api/spielen/begin", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            attemptId: session.id,
            nonce: session.nonce
          })
        }).then(async (response) => {
          if (!response.ok) {
            throw new Error("begin failed");
          }

          const data = await response.json();
          serverStarted = true;
          if (data.endsAt && data.serverNow) {
            const serverRemaining = Date.parse(data.endsAt) - Date.parse(data.serverNow);
            if (Number.isFinite(serverRemaining)) {
              deadlineAt = performance.now() + Math.max(0, serverRemaining);
            }
          }
          return true;
        }).finally(() => {
          beginPromise = null;
        });
      }

      try {
        await beginPromise;
        return true;
      } catch {
        result.textContent = "Der Versuch konnte nicht gestartet werden.";
        prepared = false;
        finished = true;
        input.disabled = true;
        startButton.hidden = false;
        startButton.disabled = false;
        startButton.textContent = "Erneut versuchen";
        return false;
      }
    };

    const stopTimer = () => {
      cancelAnimationFrame(timerFrame);
      if (startedAt !== null) {
        const elapsed = performance.now() - startedAt;
        timerValue.textContent = formatDuration(elapsed);
        timerLabel.textContent = "Dauer";
      }
    };

    const splitGraphemes = (value) => {
      const normalized = normalizeTypingText(value);
      if (window.Intl && Intl.Segmenter) {
        return Array.from(new Intl.Segmenter("de", { granularity: "grapheme" }).segment(normalized), segment => segment.segment);
      }

      return Array.from(normalized);
    };

    const countCompletedWords = (value) => {
      const normalized = normalizeTypingText(value);
      const words = normalized.trim().split(/\s+/u).filter(Boolean);
      if (words.length === 0) {
        return 0;
      }

      return /\s$/u.test(normalized) ? words.length : Math.max(0, words.length - 1);
    };

    const noteCompletedWords = () => {
      if (startedAt === null || wordDurationsMilliseconds.length >= 200) {
        return;
      }

      const completedWords = countCompletedWords(input.value);
      const now = performance.now();
      while (lastCompletedWordCount < completedWords && wordDurationsMilliseconds.length < 200) {
        wordDurationsMilliseconds.push(Math.max(1, Math.round(now - (lastWordBoundaryAt ?? startedAt))));
        lastWordBoundaryAt = now;
        lastCompletedWordCount += 1;
      }
    };

    const completePendingWord = () => {
      if (startedAt === null || wordDurationsMilliseconds.length >= 200) {
        return;
      }

      const words = String(input.value || "").trim().split(/\s+/u).filter(Boolean);
      if (words.length > lastCompletedWordCount) {
        const now = performance.now();
        wordDurationsMilliseconds.push(Math.max(1, Math.round(now - (lastWordBoundaryAt ?? startedAt))));
        lastWordBoundaryAt = now;
        lastCompletedWordCount = words.length;
      }
    };

    const collectFinalErrors = () => {
      if (!session) {
        return [];
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(session.text);
      const length = typed.length;
      const errors = [];

      for (let index = 0; index < length; index += 1) {
        if (typed[index] !== expected[index]) {
          errors.push({
            index,
            expected: expected[index] || "∅",
            actual: typed[index] || "∅"
          });
        }
      }

      return errors;
    };

    const noteMistake = () => {
      if (!session || input.value.length === 0) {
        return;
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(session.text);
      const index = typed.length - 1;
      if (index < 0 || typed[index] === expected[index]) {
        return;
      }

      const key = `${index}:${expected[index] || "∅"}:${typed[index] || "∅"}`;
      const current = mistakeMap.get(key) || {
        index,
        expected: expected[index] || "∅",
        actual: typed[index] || "∅",
        count: 0
      };
      current.count += 1;
      mistakeMap.set(key, current);
    };

    const renderAnalysis = (data) => {
      const observed = [...mistakeMap.values()].sort((left, right) => right.count - left.count).slice(0, 5);
      const finalErrors = collectFinalErrors().slice(0, 5);
      const observedRows = observed.map(item => `<li>Position ${item.index + 1}: ${escapeHtml(item.expected)} erwartet, ${escapeHtml(item.actual)} getippt (${item.count}x)</li>`).join("");
      const finalRows = finalErrors.map(item => `<li>Position ${item.index + 1}: ${escapeHtml(item.expected)} erwartet, ${escapeHtml(item.actual)} im Ergebnis</li>`).join("");
      const status = targetWasCompleted(data)
        ? "Zieltext fehlerfrei abgeschlossen"
        : isTimed() ? "Sprintzeit beendet" : "Zieltext nicht fehlerfrei abgeschlossen";
      analysis.innerHTML = `<h3>Fehleranalyse</h3>
        <div class="analysis-grid">
          <div><span>Status</span><strong>${status}</strong></div>
          <div><span>Fehlerzeichen</span><strong>${data.incorrectCharacters}</strong></div>
          <div><span>Korrekturen</span><strong>${backspaces}</strong></div>
          <div><span>Fokusverlust</span><strong>${focusLosses}</strong></div>
        </div>
        <h4>Während der Eingabe</h4>
        ${observedRows ? `<ul>${observedRows}</ul>` : "<p>Keine Abweichungen beobachtet.</p>"}
        <h4>Im Endergebnis</h4>
        ${finalRows ? `<ul>${finalRows}</ul>` : "<p>Keine verbleibenden Fehler im Zieltext.</p>"}`;
    };

    const renderMotivation = (motivation) => {
      if (!motivation) {
        return "";
      }

      const events = Array.isArray(motivation.events) ? motivation.events : [];
      if ((motivation.xpDelta || 0) <= 0 && events.length === 0 && motivation.levelAfter <= motivation.levelBefore) {
        return "";
      }

      const levelUp = Number(motivation.levelAfter || 0) > Number(motivation.levelBefore || 0);
      const eventRows = events.map((item) => {
        const rarity = String(item.rarity || "Common").toLowerCase();
        const visualKey = safeVisualKey(item.visualKey || "xp");
        const accent = safeVisualKey(item.accent || rarity || "common");
        const xp = Number(item.xpDelta || 0) > 0 ? `<span class="xp-chip">+${numberFormat.format(item.xpDelta)} XP</span>` : "";
        return `<div class="motivation-event rarity-${escapeHtml(rarity)} accent-${escapeHtml(accent)}">
          <span class="motivation-event-icon" aria-hidden="true">${iconSvg(visualKey)}</span>
          <span class="motivation-event-copy">
            <strong>${escapeHtml(item.title || "Reward")}</strong>
            <span>${escapeHtml(item.description || "")}</span>
          </span>
          ${xp}
        </div>`;
      }).join("");

      const headline = levelUp
        ? `Level ${escapeHtml(motivation.levelAfter)} erreicht`
        : `${numberFormat.format(motivation.xpDelta || 0)} XP erhalten`;
      const subline = levelUp
        ? `Von Level ${escapeHtml(motivation.levelBefore)} auf Level ${escapeHtml(motivation.levelAfter)}.`
        : "Fortschritt wurde gespeichert.";

      return `<section class="motivation-panel ${levelUp ? "level-up" : ""}">
        <img class="motivation-burst" src="/vendor/keywars-assets/illustrations/reward-burst.svg" alt="" width="180" height="120" loading="lazy">
        <div class="motivation-header">
          <span class="motivation-level-icon" aria-hidden="true">${iconSvg(levelUp ? "level-up" : "xp")}</span>
          <div>
            <h3>${headline}</h3>
            <p class="muted">${subline}</p>
          </div>
          ${motivation.xpDelta > 0 ? `<span class="xp-chip">+${numberFormat.format(motivation.xpDelta)} XP</span>` : ""}
        </div>
        ${eventRows ? `<div class="motivation-events">${eventRows}</div>` : ""}
      </section>`;
    };

    const finish = async () => {
      if (!session || finishing || finished) {
        return;
      }

      finishing = true;
      finished = true;
      root.classList.remove("typing-prepared", "typing-running");
      root.classList.add("typing-finished");
      stopTimer();
      input.disabled = true;
      if (input.value.length > 0 && !(await beginAttempt())) {
        finishing = false;
        return;
      }

      completePendingWord();
      const payload = {
        attemptId: session.id,
        nonce: session.nonce,
        input: input.value,
        backspaces,
        focusLosses,
        clientDurationMilliseconds: Math.max(1, Math.round(performance.now() - (startedAt ?? performance.now()))),
        wordDurationsMilliseconds
      };
      const endpoint = challengeId ? `/api/herausforderungen/${challengeId}/abschliessen` : "/api/spielen/abschliessen";
      let response;
      try {
        response = await postFinishWithRetry(endpoint, payload);
      } catch {
        result.textContent = "Der Versuch konnte nicht gespeichert werden. Die Verbindung wird beim nächsten Abschluss sicher erneut geprüft.";
        finishing = false;
        finished = false;
        input.disabled = false;
        root.classList.remove("typing-finished");
        root.classList.add("typing-running");
        return;
      }

      if (!response.ok) {
        const problem = await readProblem(response);
        if (response.status === 409 && problem.code === "attempt_still_running") {
          const retryAfterMs = Math.max(1, Number(problem.retryAfterMs) || 250);
          deadlineAt = performance.now() + retryAfterMs;
          result.textContent = `Der Sprint läuft serverseitig noch ${Math.ceil(retryAfterMs / 1000)} s.`;
          finishing = false;
          finished = false;
          input.disabled = false;
          root.classList.remove("typing-finished");
          root.classList.add("typing-running");
          timerFrame = requestAnimationFrame(updateTimer);
          return;
        }

        result.textContent = problem.title || "Der Versuch konnte nicht gespeichert werden.";
        finishing = false;
        finished = false;
        input.disabled = false;
        return;
      }

      const data = await response.json();
      const targetCompleted = targetWasCompleted(data);
      const consistencySampleCount = Math.max(0, Number(data.consistencySampleCount) || 0);
      const consistencyValue = consistencySampleCount >= 2
        ? `${numberFormat.format(data.consistency)} %`
        : "–";
      const correctCharacters = Math.max(0, Number(data.correctCharacters) || 0);
      const incorrectCharacters = Math.max(0, Number(data.incorrectCharacters) || 0);
      const attemptedCharacters = correctCharacters + incorrectCharacters;
      const durationSeconds = Math.max(0, Number(data.durationMilliseconds) || 0) / 1000;
      const progressPercent = Number.isFinite(data.progressPercent) ? Math.max(0, Math.min(100, data.progressPercent)) : 0;
      const personalBest = Array.isArray(data.motivation?.events) &&
        data.motivation.events.some((item) => item.type === "PersonalBest");
      const finishTitle = personalBest
        ? "Neuer Bestwert"
        : targetCompleted ? "Zieltext fehlerfrei abgeschlossen" : isTimed() ? "Sprint abgeschlossen" : "Runde gewertet";
      const finishDetail = targetCompleted
        ? "Der Zieltext wurde vollständig und fehlerfrei erreicht."
        : isTimed()
          ? "Die Zeit ist abgelaufen. Dein bis dahin erreichter Stand wurde gewertet."
          : `${incorrectCharacters} Fehlerzeichen bleiben im Ergebnis sichtbar.`;
      const durationNote = durationSeconds > 0
        ? `WPM: ${numberFormat.format(correctCharacters)} korrekte Zeichen ÷ 5 in ${numberFormat.format(durationSeconds)} s.`
        : "WPM basiert auf korrekten Zeichen und der gewerteten Dauer.";
      const accuracyNote = `Genauigkeit: ${numberFormat.format(correctCharacters)} korrekte von ${numberFormat.format(attemptedCharacters)} gewerteten Zeichen; ${numberFormat.format(incorrectCharacters)} Fehlerzeichen.`;
      const consistencyNote = consistencySampleCount >= 2
        ? `Konsistenz aus ${numberFormat.format(consistencySampleCount)} abgeschlossenen Wortzeiten.`
        : "Für eine Konsistenzwertung sind mindestens zwei abgeschlossene Wortzeiten nötig.";
      updateRoundStats(data);
      result.innerHTML = `<section class="finish-panel ${personalBest ? "personal-best" : ""}">
        <div class="finish-score">
          <span>${finishTitle}</span>
          <strong>${numberFormat.format(data.wpm)}</strong>
          <small>WPM</small>
        </div>
        <div class="finish-summary">
          <p>${finishDetail}</p>
          <div class="metric-row result-metrics">
            <div class="metric"><span>Genauigkeit</span><strong>${numberFormat.format(data.accuracy)} %</strong></div>
            <div class="metric"><span>Konsistenz</span><strong>${consistencyValue}</strong></div>
            <div class="metric"><span>Korrekte Zeichen</span><strong>${numberFormat.format(correctCharacters)}</strong></div>
            <div class="metric"><span>Fehlerzeichen</span><strong>${numberFormat.format(incorrectCharacters)}</strong></div>
          </div>
        </div>
      </section>
      <section class="xp-reveal" aria-label="Level-Fortschritt">
        <div>
          <span>XP gesamt</span>
          <strong>${numberFormat.format(data.experiencePoints)}</strong>
        </div>
        <div class="xp-reveal-bar">
          <progress class="progress xp-progress" value="${progressPercent}" max="100" aria-label="Fortschritt bis zum nächsten Level">${progressPercent} %</progress>
          <span>Noch ${numberFormat.format(data.remainingXp)} XP bis Level ${data.level + 1}.</span>
        </div>
      </section>
      ${renderMotivation(data.motivation)}
      <p class="metric-note">${durationNote} ${accuracyNote} ${consistencyNote}</p>`;
      result.append(analysis);
      renderAnalysis(data);
      session = null;
      startButton.hidden = false;
      startButton.disabled = false;
      startButton.textContent = finishedStartLabel;
      if (challengeId) {
        startButton.disabled = true;
      }
    };

    const renderIdle = () => {
      root.classList.add("typing-idle");
      root.classList.remove("typing-prepared", "typing-running", "typing-finished");
      prepared = false;
      finishing = false;
      finished = false;
      session = null;
      serverStarted = false;
      beginPromise = null;
      input.value = "";
      input.disabled = true;
      result.textContent = "";
      analysis.textContent = "";
      backspaces = 0;
      focusLosses = 0;
      mistakeMap.clear();
      resetTimer();
      target.textContent = idleMessage;
      startButton.hidden = false;
      startButton.disabled = false;
      startButton.textContent = initialStartLabel;
    };

    const prepare = async () => {
      root.classList.remove("typing-idle");
      root.classList.remove("typing-prepared", "typing-running", "typing-finished");
      prepared = false;
      finishing = false;
      finished = false;
      session = null;
      serverStarted = false;
      beginPromise = null;
      lastCompletedWordCount = 0;
      lastWordBoundaryAt = null;
      wordDurationsMilliseconds.length = 0;
      input.value = "";
      input.disabled = true;
      result.textContent = "";
      analysis.textContent = "";
      backspaces = 0;
      focusLosses = 0;
      mistakeMap.clear();
      resetTimer();
      render();
      startButton.disabled = true;
      startButton.textContent = "Lädt";

      const startEndpoint = challengeId ? `/api/herausforderungen/${challengeId}/start` : "/api/spielen/start";
      const response = await fetch(startEndpoint, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(request())
      });
      if (!response.ok) {
        target.textContent = "Die Runde konnte nicht vorbereitet werden.";
        startButton.hidden = false;
        startButton.disabled = false;
        startButton.textContent = "Erneut versuchen";
        return;
      }

      session = await response.json();
      input.value = "";
      input.disabled = false;
      prepared = true;
      root.classList.add("typing-prepared");
      resetTimer();
      startButton.hidden = hideStartWhenPrepared;
      startButton.disabled = challengeId;
      startButton.textContent = preparedStartLabel;
      render();
      resetTypingScroll(target);
    };

    startButton.addEventListener("click", async () => {
      await prepare();
    });

    input.addEventListener("keydown", (event) => {
      if (event.key === "Backspace") {
        backspaces += 1;
      }
    });
    input.addEventListener("paste", (event) => event.preventDefault());
    input.addEventListener("drop", (event) => event.preventDefault());
    input.addEventListener("blur", () => { focusLosses += 1; });
    input.addEventListener("input", async () => {
      if (!prepared || !session || finishing || finished) {
        return;
      }

      if (input.value.length > 0) {
        startTimer();
        if (!(await beginAttempt())) {
          return;
        }

        noteMistake();
        noteCompletedWords();
      }

      render();
      const typedLength = splitGraphemes(input.value).length;
      const expectedLength = splitGraphemes(session.text).length;
      if (typedLength >= expectedLength) {
        finish();
      }
    });

    if (autoPrepare) {
      prepare();
    } else {
      renderIdle();
    }
  });
}

async function postFinishWithRetry(endpoint, payload) {
  let lastError;
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(payload)
      });
      if (response.status < 500 || attempt > 0) {
        return response;
      }
    } catch (error) {
      lastError = error;
      if (attempt > 0) {
        throw error;
      }
    }

    await new Promise((resolve) => window.setTimeout(resolve, 250));
  }

  throw lastError || new Error("Finish fehlgeschlagen.");
}

async function readProblem(response) {
  try {
    return await response.json();
  } catch {
    return {};
  }
}

function sprintSecondsFromMode(mode) {
  const match = /^Sprint(\d+)$/.exec(mode || "");
  return match ? Number(match[1]) : 0;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function safeVisualKey(value) {
  const key = String(value || "").toLowerCase();
  return /^[a-z0-9-]+$/.test(key) ? key : "xp";
}

function iconSvg(key) {
  return `<svg class="kw-icon" aria-hidden="true" focusable="false"><use href="/vendor/keywars-assets/keywars-icons.svg#kw-${safeVisualKey(key)}"></use></svg>`;
}

function normalizeTypingText(value) {
  return String(value || "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .normalize("NFC");
}

function renderTypingCharacters(container, expected, classForIndex) {
  const nodes = [];
  expected.forEach((char, index) => {
    const span = document.createElement("span");
    const stateClass = classForIndex(char, index);
    if (stateClass) {
      span.className = stateClass;
    }

    if (char === "\n") {
      span.textContent = "\u21b5";
      span.classList.add("typing-newline");
      span.title = "Absatz: Enter drücken";
      span.setAttribute("aria-label", "Absatz: Enter drücken");
      nodes.push(span, document.createElement("br"));
      return;
    }

    span.textContent = char;
    nodes.push(span);
  });
  container.replaceChildren(...nodes);
}
