import { splitGraphemes } from "./typing-text.js";

export function tableCell(content) {
  const cell = document.createElement("td");
  if (content instanceof Node) {
    cell.append(content);
  } else {
    cell.textContent = content;
  }

  return cell;
}

export function statusPill(status) {
  const span = document.createElement("span");
  span.className = "pill";
  span.textContent = statusLabel(status);
  return span;
}

export function badge(text) {
  const span = document.createElement("span");
  span.className = "badge";
  span.textContent = text;
  return span;
}

export function element(tagName, text) {
  const node = document.createElement(tagName);
  node.textContent = text;
  return node;
}

export function setText(node, text) {
  if (node) {
    node.textContent = text;
  }
}

export function setStatusText(node, text) {
  if (node && node.textContent !== text) {
    node.textContent = text;
  }
}

export function setHidden(node, hidden) {
  if (node) {
    node.classList.toggle("is-hidden", hidden);
  }
}

export function initials(value) {
  const parts = String(value || "")
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2);
  if (parts.length === 0) {
    return "KW";
  }

  return parts.map((part) => part[0].toUpperCase()).join("");
}

export function formatNumber(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "-";
  }

  return new Intl.NumberFormat("de-DE", {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1
  }).format(value);
}

export function statusLabel(status) {
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

export function phaseLabel(phase) {
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

export function teamName(teamNumber) {
  return Number(teamNumber) === 1 ? "Alpha" : Number(teamNumber) === 2 ? "Bravo" : "-";
}

export function normalizePersistenceState(value) {
  const normalized = String(value || "").toLowerCase();
  return {
    inactive: "Inactive",
    running: "Running",
    finishedpending: "FinishedPending",
    pending: "Pending",
    persisted: "Persisted",
    failed: "Failed",
    abortedunconfirmed: "AbortedUnconfirmed"
  }[normalized] || null;
}

export function persistenceStateFor(currentSnapshot) {
  if (!currentSnapshot) {
    return "Inactive";
  }

  if (!currentSnapshot.finished) {
    return currentSnapshot.phase === "Running" ? "Running" : "Inactive";
  }

  return normalizePersistenceState(currentSnapshot.persistenceState) || "FinishedPending";
}

export function isPendingPersistenceState(state) {
  return state === "Pending" || state === "FinishedPending";
}

export function persistenceStatusText(state, pollingExhausted = false) {
  const text = {
    Running: "Ergebnisstatus: Rennen läuft.",
    FinishedPending: "Ergebnis vorläufig: Speicherung läuft. Rating und XP werden erst nach Bestätigung angezeigt.",
    Pending: "Ergebnis vorläufig: Speicherung läuft. Rating und XP werden erst nach Bestätigung angezeigt.",
    Persisted: "Ergebnis gespeichert. Rating und XP sind bestätigt.",
    Failed: "Speicherung fehlgeschlagen. Rating und XP wurden nicht vergeben.",
    AbortedUnconfirmed: "Ergebnis nach Serverabbruch unbestätigt. Rating und XP wurden nicht vergeben."
  }[state] || "";
  if (pollingExhausted && isPendingPersistenceState(state)) {
    return `${text} Automatische Prüfung beendet; lade die Seite für einen neuen Status neu.`;
  }

  return text;
}

export function podiumTitle(state) {
  return {
    Pending: "Podium (vorläufig)",
    FinishedPending: "Podium (vorläufig)",
    Failed: "Podium (Speicherung fehlgeschlagen)",
    AbortedUnconfirmed: "Podium (unbestätigt)"
  }[state] || "Podium";
}

export function connectionStatusText(state) {
  return {
    connected: "Verbindung: aktiv",
    reconnecting: "Verbindung: wird wiederhergestellt",
    disconnected: "Verbindung: getrennt"
  }[state] || "Verbindung: getrennt";
}

export function formatDuration(milliseconds) {
  const value = Math.max(0, milliseconds);
  const minutes = Math.floor(value / 60000);
  const seconds = Math.floor((value % 60000) / 1000);
  const tenths = Math.floor((value % 1000) / 100);
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${tenths}`;
}

export function isExactInput(input, target) {
  const inputElements = splitGraphemes(input);
  const targetElements = splitGraphemes(target);
  return inputElements.length === targetElements.length &&
    inputElements.every((entry, index) => entry === targetElements[index]);
}

export function textSpan(text, className) {
  const span = document.createElement("span");
  span.textContent = text;
  span.className = className;
  return span;
}

export function camelize(value) {
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

export function showConnectionError(error) {
  const message = error instanceof Error ? error.message : "Arena-Aktion fehlgeschlagen.";
  const alert = document.querySelector("[data-arena-error]") || document.createElement("div");
  alert.dataset.arenaError = "true";
  alert.className = "alert";
  alert.textContent = message;
  document.querySelector("[data-arena-room]")?.before(alert);
}
