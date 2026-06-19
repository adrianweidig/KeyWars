export function attachArenaPages() {
  document.querySelectorAll("[data-arena-room]").forEach((root) => {
    const roomId = root.dataset.roomId;
    const currentProfileId = root.dataset.currentProfileId;
    const target = root.querySelector("[data-arena-target]");
    const input = root.querySelector("[data-arena-input]");
    const participants = root.querySelector("[data-arena-participants]");
    const state = root.querySelector("[data-arena-state]");
    const timer = root.querySelector("[data-arena-timer]");
    const readyForm = document.querySelector("[data-arena-ready-form]");
    const startForm = document.querySelector("[data-arena-start-form]");
    const dnfButton = root.querySelector("[data-arena-dnf]");
    const leaveButton = document.querySelector("[data-arena-leave]");
    const connection = new SignalRConnection("/hubs/arena");

    let snapshot = null;
    let sequence = 0;
    let progressTimer = 0;
    let timerFrame = 0;
    let startRefreshTimer = 0;
    let backspaces = 0;
    let focusLosses = 0;
    let finishedLocally = false;

    const renderTarget = () => {
      if (!snapshot || !target || !input) {
        return;
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(snapshot.targetText || "");
      if (expected.length === 0) {
        target.replaceChildren(textSpan("Der Text wird zum Start freigegeben.", "muted"));
        return;
      }

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

    const renderParticipants = () => {
      if (!snapshot || !participants) {
        return;
      }

      participants.replaceChildren(...snapshot.participants.map((participant) => {
        const row = document.createElement("tr");
        row.append(
          tableCell(participant.displayName),
          tableCell(statusPill(participant.status)),
          tableCell(`${participant.correctCharacters} / ${snapshot.targetCharacterCount}`),
          tableCell(participant.placement ? String(participant.placement) : participant.rankHint ? `~${participant.rankHint}` : "-")
        );
        return row;
      }));
    };

    const renderState = () => {
      if (!snapshot) {
        return;
      }

      const running = snapshot.phase === "Running";
      if (state) {
        state.textContent = phaseLabel(snapshot.phase);
      }

      if (input) {
        input.disabled = !running || finishedLocally || !snapshot.targetText;
      }

      if (dnfButton) {
        dnfButton.disabled = !running || finishedLocally;
      }

      const current = snapshot.participants?.find((participant) => participant.profileId === currentProfileId);
      const readyButton = readyForm?.querySelector("button");
      if (readyButton) {
        readyButton.textContent = current?.ready ? "Nicht bereit" : "Bereit";
        readyButton.disabled = !snapshot || snapshot.phase !== "Lobby";
      }

      const startButton = startForm?.querySelector("button");
      if (startButton) {
        startButton.disabled = !snapshot || snapshot.phase !== "Lobby";
      }

      renderTimer();
    };

    const renderTimer = () => {
      if (!timer || !snapshot) {
        return;
      }

      cancelAnimationFrame(timerFrame);
      const value = timer.querySelector("strong");
      const label = timer.querySelector("span");
      if (!value || !label) {
        return;
      }

      if (snapshot.phase === "Countdown" && snapshot.raceStartsAt) {
        const raceStartsAt = new Date(snapshot.raceStartsAt).getTime();
        const serverNow = new Date(snapshot.serverNow).getTime();
        const offset = serverNow - Date.now();
        const tick = () => {
          const remaining = Math.max(0, raceStartsAt - (Date.now() + offset));
          value.textContent = remaining <= 0 ? "LOS" : Math.ceil(remaining / 1000).toString();
          label.textContent = "Start";
          if (remaining <= 0) {
            window.clearTimeout(startRefreshTimer);
            startRefreshTimer = window.setTimeout(() => {
              connection.invoke("JoinRoom", [roomId]).then(applySnapshot).catch(showConnectionError);
            }, 80);
            return;
          }

          timerFrame = requestAnimationFrame(tick);
        };
        tick();
        return;
      }

      if (!snapshot.startedAt) {
        value.textContent = "00:00.0";
        label.textContent = phaseLabel(snapshot.phase);
        return;
      }

      const startedAt = new Date(snapshot.startedAt).getTime();
      const serverNow = new Date(snapshot.serverNow).getTime();
      const localNow = Date.now();
      const offset = serverNow - localNow;
      const tick = () => {
        const end = snapshot.finishedAt ? new Date(snapshot.finishedAt).getTime() : Date.now() + offset;
        value.textContent = formatDuration(Math.max(0, end - startedAt));
        label.textContent = snapshot.finished ? "Dauer" : "vergangen";
        if (!snapshot.finished) {
          timerFrame = requestAnimationFrame(tick);
        }
      };
      tick();
    };

    const applySnapshot = (next) => {
      snapshot = camelize(next);
      renderTarget();
      renderParticipants();
      renderState();
    };

    const applyProgressBatch = (next) => {
      const batch = camelize(next);
      if (!snapshot || batch.roomId !== snapshot.roomId || !Array.isArray(batch.deltas)) {
        return;
      }

      if (batch.roomVersion < snapshot.roundVersion) {
        return;
      }

      snapshot.roundVersion = Math.max(snapshot.roundVersion, batch.roomVersion);
      batch.deltas.forEach((delta) => {
        const participant = snapshot.participants?.find((item) => item.profileId === delta.participantId);
        if (!participant) {
          return;
        }

        participant.correctCharacters = delta.correctCharacters;
        participant.wpm = delta.wpm;
        participant.accuracy = delta.accuracy;
        participant.rankHint = delta.rankHint;
      });
      renderParticipants();
      renderState();
    };

    const submitProgress = () => {
      if (!snapshot || snapshot.phase !== "Running" || snapshot.finished || finishedLocally) {
        return;
      }

      clearTimeout(progressTimer);
      progressTimer = setTimeout(() => {
        sequence += 1;
        connection.invoke("SubmitProgress", [roomId, sequence, input.value]).catch(showConnectionError);
      }, 90);
    };

    const finish = async () => {
      if (!snapshot || finishedLocally) {
        return;
      }

      finishedLocally = true;
      if (input) {
        input.disabled = true;
      }

      try {
        const next = await connection.invoke("Finish", [roomId, input.value, backspaces, focusLosses]);
        applySnapshot(next);
      } catch (error) {
        finishedLocally = false;
        showConnectionError(error);
        renderState();
      }
    };

    const giveUp = async () => {
      if (!snapshot || finishedLocally) {
        return;
      }

      finishedLocally = true;
      if (input) {
        input.disabled = true;
      }

      try {
        const next = await connection.invoke("GiveUp", [roomId]);
        applySnapshot(next);
      } catch (error) {
        finishedLocally = false;
        showConnectionError(error);
        renderState();
      }
    };

    readyForm?.addEventListener("submit", async (event) => {
      event.preventDefault();
      try {
        const current = snapshot?.participants?.find((participant) => participant.profileId === currentProfileId);
        applySnapshot(await connection.invoke("SetReady", [roomId, current?.ready !== true]));
      } catch (error) {
        showConnectionError(error);
      }
    });

    startForm?.addEventListener("submit", async (event) => {
      event.preventDefault();
      try {
        applySnapshot(await connection.invoke("Start", [roomId]));
      } catch (error) {
        showConnectionError(error);
      }
    });

    dnfButton?.addEventListener("click", () => {
      if (!window.confirm("Runde wirklich aufgeben? Das Ergebnis wird als nicht beendet gespeichert.")) {
        return;
      }

      giveUp();
    });

    leaveButton?.addEventListener("click", async () => {
      try {
        await connection.invoke("LeaveRoom", [roomId]);
      } catch (error) {
        showConnectionError(error);
        return;
      }

      window.location.href = "/arena";
    });

    input?.addEventListener("keydown", (event) => {
      if (event.key === "Backspace") {
        backspaces += 1;
      }
    });
    input?.addEventListener("blur", () => { focusLosses += 1; });
    input?.addEventListener("paste", (event) => event.preventDefault());
    input?.addEventListener("drop", (event) => event.preventDefault());
    input?.addEventListener("input", () => {
      renderTarget();
      submitProgress();
      if (snapshot && snapshot.phase === "Running" && isExactInput(input.value, snapshot.targetText)) {
        finish();
      }
    });

    connection.on("roomChanged", applySnapshot);
    connection.on("progressChanged", applyProgressBatch);
    connection.onReconnect(async () => {
      try {
        applySnapshot(await connection.invoke("JoinRoom", [roomId]));
      } catch (error) {
        showConnectionError(error);
      }
    });

    connection.start()
      .then(() => connection.invoke("JoinRoom", [roomId]))
      .then(applySnapshot)
      .catch(showConnectionError);

    window.addEventListener("pagehide", () => {
      if (connection.isConnected()) {
        connection.invoke("LeaveRoom", [roomId]).catch(() => {});
      }
    });
  });
}

class SignalRConnection {
  constructor(path) {
    if (!window.signalR) {
      throw new Error("Der lokale SignalR-Client wurde nicht geladen.");
    }

    this.reconnectHandlers = [];
    this.connection = new window.signalR.HubConnectionBuilder()
      .withUrl(path)
      .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
      .configureLogging(window.signalR.LogLevel.Warning)
      .build();
    this.connection.serverTimeoutInMilliseconds = 30000;
    this.connection.keepAliveIntervalInMilliseconds = 10000;
    this.connection.onreconnected(() => {
      this.reconnectHandlers.forEach((handler) => handler());
    });
    this.connection.onclose((error) => {
      if (error) {
        showConnectionError(error);
      }
    });
  }

  on(target, handler) {
    this.connection.on(target, handler);
  }

  onReconnect(handler) {
    this.reconnectHandlers.push(handler);
  }

  async start() {
    if (this.connection.state !== window.signalR.HubConnectionState.Disconnected) {
      return;
    }

    await this.connection.start();
  }

  invoke(target, args) {
    if (this.connection.state !== window.signalR.HubConnectionState.Connected) {
      return Promise.reject(new Error("Arena-Verbindung ist nicht aktiv."));
    }

    return this.connection.invoke(target, ...(args || []));
  }

  isConnected() {
    return this.connection.state === window.signalR.HubConnectionState.Connected;
  }
}

function tableCell(content) {
  const cell = document.createElement("td");
  if (content instanceof Node) {
    cell.append(content);
  } else {
    cell.textContent = content;
  }

  return cell;
}

function statusPill(status) {
  const span = document.createElement("span");
  span.className = "pill";
  span.textContent = statusLabel(status);
  return span;
}

function statusLabel(status) {
  return {
    Invited: "Eingeladen",
    Joined: "Beigetreten",
    Ready: "Bereit",
    Running: "Läuft",
    Finished: "Fertig",
    LeftBeforeStart: "Vor dem Start verlassen",
    Dnf: "Nicht beendet",
    Disconnected: "Verbindung getrennt",
    Declined: "Abgelehnt",
    Cancelled: "Abgebrochen",
    AbortedByServer: "Durch Serverabbruch beendet"
  }[status] || status;
}

function phaseLabel(phase) {
  return {
    Lobby: "Lobby",
    Countdown: "Countdown",
    Running: "Rennen läuft",
    RoundResults: "Rundenergebnis",
    SeriesResults: "Ergebnisse",
    Closed: "Geschlossen",
    Aborted: "Abgebrochen"
  }[phase] || "Arena";
}

function formatDuration(milliseconds) {
  const value = Math.max(0, milliseconds);
  const minutes = Math.floor(value / 60000);
  const seconds = Math.floor((value % 60000) / 1000);
  const tenths = Math.floor((value % 1000) / 100);
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${tenths}`;
}

function splitGraphemes(value) {
  const normalized = String(value || "").normalize("NFC");
  if (window.Intl && typeof window.Intl.Segmenter === "function") {
    const segmenter = new window.Intl.Segmenter("de", { granularity: "grapheme" });
    return Array.from(segmenter.segment(normalized), (segment) => segment.segment);
  }

  return Array.from(normalized);
}

function isExactInput(input, target) {
  const inputElements = splitGraphemes(input);
  const targetElements = splitGraphemes(target);
  return inputElements.length === targetElements.length &&
    inputElements.every((element, index) => element === targetElements[index]);
}

function textSpan(text, className) {
  const span = document.createElement("span");
  span.textContent = text;
  span.className = className;
  return span;
}

function camelize(value) {
  if (Array.isArray(value)) {
    return value.map(camelize);
  }

  if (!value || typeof value !== "object") {
    return value;
  }

  return Object.fromEntries(Object.entries(value).map(([key, entry]) => [
    `${key.charAt(0).toLowerCase()}${key.slice(1)}`,
    camelize(entry)
  ]));
}

function showConnectionError(error) {
  const message = error instanceof Error ? error.message : "Arena-Aktion fehlgeschlagen.";
  const alert = document.querySelector("[data-arena-error]") || document.createElement("div");
  alert.dataset.arenaError = "true";
  alert.className = "alert";
  alert.textContent = message;
  document.querySelector("[data-arena-room]")?.before(alert);
}
