export function attachTypingApps() {
  document.querySelectorAll("[data-typing-app]").forEach((root) => {
    const target = root.querySelector("[data-target]");
    const input = root.querySelector("[data-input]");
    const startButton = root.querySelector("[data-start]");
    const result = root.querySelector("[data-result]");
    const challengeId = root.dataset.challengeId || "";
    const timer = document.createElement("div");
    const timerValue = document.createElement("strong");
    const timerLabel = document.createElement("span");
    const analysis = document.createElement("div");
    timer.className = "typing-timer";
    timerValue.textContent = "00:00.0";
    timerLabel.textContent = "Bereit";
    timer.append(timerValue, timerLabel);
    analysis.className = "typing-analysis";
    target.insertAdjacentElement("afterend", timer);

    let session = null;
    let startedAt = null;
    let timerFrame = 0;
    let backspaces = 0;
    let focusLosses = 0;
    let finishing = false;
    let finished = false;
    let prepared = false;
    const mistakeMap = new Map();

    const request = () => ({
      mode: root.dataset.mode || "Sprint60",
      trainingTextId: root.dataset.textId || null,
      sprintSeconds: Number(root.dataset.seconds || sprintSecondsFromMode(root.dataset.mode) || "0"),
      wordCount: Number(root.dataset.words || "80")
    });

    const timedSeconds = () => request().sprintSeconds;
    const isTimed = () => timedSeconds() > 0 && (request().mode || "").startsWith("Sprint");

    const render = () => {
      if (!session) {
        target.textContent = "Runde wird vorbereitet.";
        return;
      }

      const typed = Array.from(input.value.normalize("NFC"));
      const expected = Array.from(session.text.normalize("NFC"));
      target.replaceChildren(...expected.map((char, index) => {
        const span = document.createElement("span");
        span.textContent = char;
        if (index < typed.length) {
          span.className = typed[index] === char ? "correct" : "wrong";
        } else if (index === typed.length) {
          span.className = "current";
        }
        return span;
      }));
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

      const elapsed = performance.now() - startedAt;
      if (isTimed()) {
        const remaining = (timedSeconds() * 1000) - elapsed;
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
      timerLabel.textContent = isTimed() ? "verbleibend" : "vergangen";
      timerFrame = requestAnimationFrame(updateTimer);
    };

    const resetTimer = () => {
      cancelAnimationFrame(timerFrame);
      startedAt = null;
      timerValue.textContent = isTimed() ? formatDuration(timedSeconds() * 1000) : "00:00.0";
      timerLabel.textContent = "Bereit";
    };

    const stopTimer = () => {
      cancelAnimationFrame(timerFrame);
      if (startedAt !== null) {
        const elapsed = performance.now() - startedAt;
        timerValue.textContent = formatDuration(elapsed);
        timerLabel.textContent = "Dauer";
      }
    };

    const splitGraphemes = (value) => Array.from(value.normalize("NFC"));

    const collectFinalErrors = () => {
      if (!session) {
        return [];
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(session.text);
      const length = Math.max(typed.length, expected.length);
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
      const status = data.completed ? "Zieltext abgeschlossen" : "Zieltext nicht fehlerfrei abgeschlossen";
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

    const finish = async () => {
      if (!session || finishing || finished) {
        return;
      }

      finishing = true;
      finished = true;
      stopTimer();
      input.disabled = true;
      const payload = {
        attemptId: session.id,
        input: input.value,
        backspaces,
        focusLosses,
        clientDurationMilliseconds: Math.max(1, Math.round(performance.now() - (startedAt ?? performance.now())))
      };
      const endpoint = challengeId ? `/api/herausforderungen/${challengeId}/abschliessen` : "/api/spielen/abschliessen";
      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(payload)
      });
      if (!response.ok) {
        result.textContent = "Der Versuch konnte nicht gespeichert werden.";
        finishing = false;
        finished = false;
        input.disabled = false;
        return;
      }

      const data = await response.json();
      result.innerHTML = `<div class="metric-row">
        <div class="metric"><span>WPM</span><strong>${data.wpm}</strong></div>
        <div class="metric"><span>Genauigkeit</span><strong>${data.accuracy} %</strong></div>
        <div class="metric"><span>Konsistenz</span><strong>${data.consistency} %</strong></div>
        <div class="metric"><span>Korrekte Zeichen</span><strong>${data.correctCharacters}</strong></div>
      </div>`;
      result.append(analysis);
      renderAnalysis(data);
      session = null;
      startButton.disabled = false;
      startButton.textContent = challengeId ? "Abgeschlossen" : "Neue Runde";
      if (challengeId) {
        startButton.disabled = true;
      }
    };

    const prepare = async () => {
      prepared = false;
      finishing = false;
      finished = false;
      session = null;
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

      const response = await fetch("/api/spielen/start", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(request())
      });
      if (!response.ok) {
        target.textContent = "Die Runde konnte nicht vorbereitet werden.";
        startButton.disabled = false;
        startButton.textContent = "Erneut versuchen";
        return;
      }

      session = await response.json();
      input.value = "";
      input.disabled = false;
      prepared = true;
      resetTimer();
      startButton.disabled = challengeId;
      startButton.textContent = challengeId ? "Bereit" : "Neue Runde";
      render();
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
    input.addEventListener("input", () => {
      if (!prepared || !session || finishing || finished) {
        return;
      }

      if (input.value.length > 0) {
        startTimer();
        noteMistake();
      }

      render();
      const typedLength = splitGraphemes(input.value).length;
      const expectedLength = splitGraphemes(session.text).length;
      if (typedLength >= expectedLength) {
        finish();
      }
    });

    prepare();
  });
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
