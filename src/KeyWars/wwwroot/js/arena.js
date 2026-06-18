const recordSeparator = "\u001e";

export function attachArenaPages() {
  document.querySelectorAll("[data-arena-room]").forEach((root) => {
    const roomId = root.dataset.roomId;
    const target = root.querySelector("[data-arena-target]");
    const input = root.querySelector("[data-arena-input]");
    const participants = root.querySelector("[data-arena-participants]");
    const state = root.querySelector("[data-arena-state]");
    const timer = root.querySelector("[data-arena-timer]");
    const readyForm = document.querySelector("[data-arena-ready-form]");
    const startForm = document.querySelector("[data-arena-start-form]");
    const finishForm = root.querySelector("[data-arena-finish-form]");
    const finishButton = root.querySelector("[data-arena-finish]");
    const connection = new JsonHubConnection("/hubs/arena");

    let snapshot = null;
    let sequence = 0;
    let progressTimer = 0;
    let timerFrame = 0;
    let backspaces = 0;
    let focusLosses = 0;
    let finishedLocally = false;

    const renderTarget = () => {
      if (!snapshot || !target || !input) {
        return;
      }

      const typed = splitGraphemes(input.value);
      const expected = splitGraphemes(snapshot.targetText || "");
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
          tableCell(participant.placement ? String(participant.placement) : "-")
        );
        return row;
      }));
    };

    const renderState = () => {
      if (!snapshot) {
        return;
      }

      const running = snapshot.started && !snapshot.finished;
      const finished = snapshot.finished;
      if (state) {
        state.textContent = finished ? "Ergebnisse" : running ? "Rennen läuft" : "Lobby";
      }

      if (input) {
        input.disabled = !running || finishedLocally;
      }

      if (finishButton) {
        finishButton.disabled = !running || finishedLocally;
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

      if (!snapshot.startedAt) {
        value.textContent = "00:00.0";
        label.textContent = "Bereit";
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

    const submitProgress = () => {
      if (!snapshot || !snapshot.started || snapshot.finished || finishedLocally) {
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

    readyForm?.addEventListener("submit", async (event) => {
      event.preventDefault();
      try {
        applySnapshot(await connection.invoke("SetReady", [roomId, true]));
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

    finishForm?.addEventListener("submit", (event) => {
      event.preventDefault();
      finish();
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
      if (snapshot && splitGraphemes(input.value).length >= snapshot.targetCharacterCount) {
        finish();
      }
    });

    connection.on("roomChanged", applySnapshot);
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
  });
}

class JsonHubConnection {
  constructor(path) {
    this.path = path;
    this.socket = null;
    this.invocationId = 0;
    this.handlers = new Map();
    this.pending = new Map();
    this.reconnectHandlers = [];
    this.stopped = false;
  }

  on(target, handler) {
    this.handlers.set(target, handler);
  }

  onReconnect(handler) {
    this.reconnectHandlers.push(handler);
  }

  async start() {
    this.stopped = false;
    const negotiate = await fetch(`${this.path}/negotiate?negotiateVersion=1`, {
      method: "POST",
      credentials: "same-origin"
    });
    if (!negotiate.ok) {
      throw new Error("Arena-Verbindung konnte nicht vorbereitet werden.");
    }

    const payload = await negotiate.json();
    const token = encodeURIComponent(payload.connectionToken || payload.connectionId);
    const scheme = window.location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(`${scheme}://${window.location.host}${this.path}?id=${token}`);
    this.socket = socket;

    await new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => reject(new Error("Arena-Verbindung hat nicht geantwortet.")), 5000);
      socket.addEventListener("open", () => {
        socket.send(`${JSON.stringify({ protocol: "json", version: 1 })}${recordSeparator}`);
      });
      socket.addEventListener("message", (event) => {
        const frames = splitFrames(event.data);
        if (frames.some((frame) => !frame || frame === "{}")) {
          window.clearTimeout(timeout);
          resolve();
        }

        this.handleFrames(frames.filter((frame) => frame && frame !== "{}"));
      });
      socket.addEventListener("error", () => reject(new Error("Arena-Verbindung fehlgeschlagen.")), { once: true });
      socket.addEventListener("close", () => this.reconnect());
    });
  }

  invoke(target, args) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error("Arena-Verbindung ist nicht aktiv."));
    }

    const invocationId = String(++this.invocationId);
    const message = { type: 1, invocationId, target, arguments: args };
    this.socket.send(`${JSON.stringify(message)}${recordSeparator}`);
    return new Promise((resolve, reject) => {
      this.pending.set(invocationId, { resolve, reject });
      window.setTimeout(() => {
        if (this.pending.delete(invocationId)) {
          reject(new Error("Arena-Anfrage hat zu lange gedauert."));
        }
      }, 8000);
    });
  }

  handleFrames(frames) {
    frames.forEach((frame) => {
      const message = JSON.parse(frame);
      if (message.type === 1) {
        const handler = this.handlers.get(message.target);
        if (handler) {
          handler(...(message.arguments || []));
        }
      } else if (message.type === 3) {
        const pending = this.pending.get(message.invocationId);
        if (!pending) {
          return;
        }

        this.pending.delete(message.invocationId);
        if (message.error) {
          pending.reject(new Error(message.error));
        } else {
          pending.resolve(message.result);
        }
      }
    });
  }

  reconnect() {
    if (this.stopped) {
      return;
    }

    window.setTimeout(() => {
      this.start()
        .then(() => Promise.all(this.reconnectHandlers.map((handler) => handler())))
        .catch(() => this.reconnect());
    }, 1200);
  }
}

function splitFrames(payload) {
  return String(payload).split(recordSeparator).filter((frame) => frame.length > 0);
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
    Dnf: "Nicht beendet",
    Disconnected: "Verbindung getrennt",
    Declined: "Abgelehnt"
  }[status] || status;
}

function formatDuration(milliseconds) {
  const value = Math.max(0, milliseconds);
  const minutes = Math.floor(value / 60000);
  const seconds = Math.floor((value % 60000) / 1000);
  const tenths = Math.floor((value % 1000) / 100);
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${tenths}`;
}

function splitGraphemes(value) {
  return Array.from(String(value || "").normalize("NFC"));
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
