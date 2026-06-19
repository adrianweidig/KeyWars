export function attachArenaPages() {
  document.querySelectorAll("[data-arena-room]").forEach((root) => {
    const roomId = root.dataset.roomId;
    const currentProfileId = root.dataset.currentProfileId;
    const target = root.querySelector("[data-arena-target]");
    const input = root.querySelector("[data-arena-input]");
    const participants = root.querySelector("[data-arena-participants]");
    const state = root.querySelector("[data-arena-state]");
    const timer = root.querySelector("[data-arena-timer]");
    const track = root.querySelector("[data-arena-track]");
    const hud = root.querySelector("[data-arena-hud]");
    const podium = root.querySelector("[data-arena-podium]");
    const liveRegion = root.querySelector("[data-arena-live-region]");
    const modeLabel = root.querySelector("[data-arena-mode-label]");
    const rosterSummaryLabel = root.querySelector("[data-arena-roster-summary]");
    const spectatorSummary = root.querySelector("[data-arena-spectator-summary]");
    const connectionQuality = root.querySelector("[data-arena-connection-quality]");
    const hiddenCountLabel = root.querySelector("[data-arena-hidden-count]");
    const windowNote = root.querySelector("[data-arena-window-note]");
    const participantList = participants?.closest("table");
    const reactionPanel = root.querySelector("[data-arena-reactions]");
    const reactionStream = root.querySelector("[data-arena-reaction-stream]");
    const readyForm = document.querySelector("[data-arena-ready-form]");
    const startForm = document.querySelector("[data-arena-start-form]");
    const dnfButton = root.querySelector("[data-arena-dnf]");
    const leaveButton = document.querySelector("[data-arena-leave]");
    const connection = new SignalRConnection("/hubs/arena");
    const showLiveWpm = root.dataset.showLiveWpm === "true";
    const showLiveRankChanges = root.dataset.showLiveRankChanges === "true";
    const soundEnabled = root.dataset.soundEnabled === "true";
    const soundVolume = Math.max(0, Math.min(1, Number(root.dataset.soundVolume || 0) / 100));
    const reactionsEnabled = root.dataset.reactionsEnabled === "true";
    const reducedMotion = root.dataset.reducedMotion === "true" ||
      window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
    root.dataset.motionReduced = reducedMotion ? "true" : "false";

    let snapshot = null;
    let sequence = 0;
    let progressTimer = 0;
    let timerFrame = 0;
    let startRefreshTimer = 0;
    let backspaces = 0;
    let focusLosses = 0;
    let finishedLocally = false;
    let previousCurrentRank = null;
    let lastRankAnnouncementAt = 0;
    let previousPhase = null;
    let audioUnlocked = false;
    let audioContext = null;

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

    const rankedParticipants = () => [...(snapshot?.participants || [])].sort((left, right) => {
      const leftPlacement = left.placement || Number.MAX_SAFE_INTEGER;
      const rightPlacement = right.placement || Number.MAX_SAFE_INTEGER;
      if (leftPlacement !== rightPlacement) {
        return leftPlacement - rightPlacement;
      }

      if (right.correctCharacters !== left.correctCharacters) {
        return right.correctCharacters - left.correctCharacters;
      }

      return String(left.displayName).localeCompare(String(right.displayName), "de");
    });

    const rankFor = (participantId) => {
      const index = rankedParticipants().findIndex((participant) => participant.profileId === participantId);
      return index < 0 ? "-" : index + 1;
    };

    const progressPercent = (participant) => {
      if (!participant || !snapshot?.targetCharacterCount) {
        return 0;
      }

      return Math.max(0, Math.min(100, participant.correctCharacters * 100 / snapshot.targetCharacterCount));
    };

    const unlockAudio = () => {
      if (!soundEnabled || audioUnlocked || soundVolume <= 0) {
        return;
      }

      const AudioContextType = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextType) {
        return;
      }

      audioUnlocked = true;
      audioContext = audioContext || new AudioContextType();
      audioContext.resume?.().catch(() => {});
    };

    const playFeedbackTone = (kind) => {
      if (!soundEnabled || !audioUnlocked || !audioContext || soundVolume <= 0) {
        return;
      }

      const tones = {
        countdown: [520, 0.07],
        start: [760, 0.11],
        rank: [920, 0.08],
        finish: [640, 0.16]
      };
      const [frequency, duration] = tones[kind] || tones.rank;
      const now = audioContext.currentTime;
      const oscillator = audioContext.createOscillator();
      const gain = audioContext.createGain();
      oscillator.type = "sine";
      oscillator.frequency.setValueAtTime(frequency, now);
      gain.gain.setValueAtTime(0, now);
      gain.gain.linearRampToValueAtTime(0.08 * soundVolume, now + 0.012);
      gain.gain.exponentialRampToValueAtTime(0.001, now + duration);
      oscillator.connect(gain).connect(audioContext.destination);
      oscillator.start(now);
      oscillator.stop(now + duration + 0.02);
    };

    const renderPhaseFeedback = () => {
      if (!snapshot) {
        return;
      }

      if (previousPhase !== null && previousPhase !== snapshot.phase) {
        if (snapshot.phase === "Countdown") {
          playFeedbackTone("countdown");
        } else if (snapshot.phase === "Running") {
          playFeedbackTone("start");
        } else if (["RoundResults", "SeriesResults", "Closed"].includes(snapshot.phase) || snapshot.finished) {
          playFeedbackTone("finish");
        }
      }

      previousPhase = snapshot.phase;
    };

    const maxParticipants = () => {
      const snapshotMax = Number(snapshot?.maxParticipants);
      const rootMax = Number(root.dataset.maxParticipants);
      if (Number.isFinite(snapshotMax) && snapshotMax > 0) {
        return snapshotMax;
      }

      return Number.isFinite(rootMax) && rootMax > 0 ? rootMax : 0;
    };

    const displayMode = () => {
      const count = snapshot?.participants?.length || 0;
      if (count <= 8) {
        return "detailed";
      }

      if (count <= 24) {
        return "compact";
      }

      return "focused";
    };

    const visibleParticipantWindow = (ranked = rankedParticipants()) => {
      if (displayMode() !== "focused") {
        return ranked;
      }

      const selected = new Set();
      ranked.slice(0, 3).forEach((participant) => selected.add(participant.profileId));
      const currentIndex = ranked.findIndex((participant) => participant.profileId === currentProfileId);
      if (currentIndex >= 0) {
        [currentIndex - 1, currentIndex, currentIndex + 1].forEach((index) => {
          if (index >= 0 && index < ranked.length) {
            selected.add(ranked[index].profileId);
          }
        });
      }

      return ranked.filter((participant) => selected.has(participant.profileId));
    };

    const modeTitle = (mode) => ({
      detailed: "Detailansicht",
      compact: "Kompakte Ansicht",
      focused: "Fokussierte Ansicht"
    }[mode] || "Arena-Ansicht");

    const rosterSummary = (total, visible) => {
      const capacity = maxParticipants();
      if (total === visible) {
        return capacity > 0
          ? `${total} aktive Teilnehmende von ${capacity} Plätzen`
          : `${total} sichtbare Teilnehmende`;
      }

      return capacity > 0
        ? `${visible} von ${total} Teilnehmenden im Fokus, Kapazität ${capacity}`
        : `${visible} von ${total} Teilnehmenden im Fokus`;
    };

    const hiddenParticipantsText = (hidden) => hidden <= 0
      ? ""
      : `${hidden} weitere Teilnehmende sind über Top-Plätze, eigene Position und Nachbarn zusammengefasst.`;

    const renderRosterMode = (ranked = rankedParticipants(), visible = visibleParticipantWindow(ranked)) => {
      const mode = displayMode();
      const hidden = Math.max(0, ranked.length - visible.length);
      root.dataset.arenaDisplayMode = mode;
      ["detailed", "compact", "focused"].forEach((name) => {
        track?.classList.toggle(name, mode === name);
        participantList?.classList.toggle(name, mode === name);
      });
      setText(modeLabel, modeTitle(mode));
      setText(rosterSummaryLabel, rosterSummary(ranked.length, visible.length));
      setText(spectatorSummary, "Zuschauer: Rolle vorbereitet");
      setText(connectionQuality, "Verbindung: aktiv");
      setText(hiddenCountLabel, hiddenParticipantsText(hidden));
      setText(windowNote, hiddenParticipantsText(hidden));
      setHidden(hiddenCountLabel, hidden === 0);
      setHidden(windowNote, hidden === 0);
    };

    const participantRow = (participant) => {
      const row = document.createElement("tr");
      row.dataset.participantId = participant.profileId;
      row.append(
        tableCell(""),
        tableCell(document.createElement("span")),
        tableCell(""),
        tableCell("")
      );
      return row;
    };

    const updateParticipantRow = (row, participant) => {
      row.cells[0].textContent = participant.displayName;
      row.cells[1].replaceChildren(statusPill(participant.status));
      row.cells[2].textContent = `${participant.correctCharacters} / ${snapshot.targetCharacterCount}`;
      row.cells[3].textContent = participant.placement ? String(participant.placement) : participant.rankHint ? `~${participant.rankHint}` : String(rankFor(participant.profileId));
    };

    const trackLane = (participant) => {
      const lane = document.createElement("div");
      lane.className = "race-lane";
      lane.dataset.trackParticipantId = participant.profileId;
      const meta = document.createElement("div");
      meta.className = "race-lane-meta";
      const bar = document.createElement("div");
      bar.className = "race-lane-bar";
      const position = document.createElement("span");
      position.className = "race-position";
      position.append(document.createElement("span"));
      bar.append(position);
      lane.append(meta, bar);
      return lane;
    };

    const updateTrackLane = (lane, participant) => {
      const meta = lane.querySelector(".race-lane-meta");
      const bar = lane.querySelector(".race-lane-bar");
      const position = lane.querySelector(".race-position");
      const percent = progressPercent(participant);
      lane.classList.toggle("current", participant.profileId === currentProfileId);
      if (meta) {
        const token = element("span", initials(participant.displayName));
        token.className = "race-token";
        const name = element("span", participant.displayName);
        const children = [token, name];
        if (participant.profileId === snapshot.creatorProfileId) {
          children.push(badge("Host"));
        }

        if (participant.ready) {
          children.push(badge("Bereit"));
        }

        meta.replaceChildren(...children);
      }

      if (bar) {
        bar.setAttribute("aria-label", `${participant.displayName}: ${Math.round(percent)} Prozent`);
      }

      if (position) {
        position.style.transform = `translateX(${percent}%)`;
        position.querySelector("span").textContent = initials(participant.displayName);
      }
    };

    const announceRankChange = (rank) => {
      if (!showLiveRankChanges || !liveRegion || rank === "-" || rank === previousCurrentRank) {
        previousCurrentRank = rank;
        return;
      }

      const now = Date.now();
      if (previousCurrentRank !== null && now - lastRankAnnouncementAt > 1500) {
        const numericRank = Number(rank);
        const previousNumericRank = Number(previousCurrentRank);
        const improved = Number.isFinite(numericRank) && Number.isFinite(previousNumericRank) && numericRank < previousNumericRank;
        liveRegion.textContent = improved
          ? `Rang verbessert auf ${rank}.`
          : `Rang jetzt ${rank}.`;
        if (improved) {
          playFeedbackTone("rank");
        }

        lastRankAnnouncementAt = now;
      }

      previousCurrentRank = rank;
    };

    const renderParticipants = () => {
      if (!snapshot || !participants) {
        return;
      }

      const expectedIds = new Set();
      const ranked = rankedParticipants();
      const visible = visibleParticipantWindow(ranked);
      renderRosterMode(ranked, visible);
      visible.forEach((participant) => {
        expectedIds.add(participant.profileId);
        const row = participants.querySelector(`[data-participant-id="${participant.profileId}"]`) || participantRow(participant);
        updateParticipantRow(row, participant);
        participants.append(row);
      });

      participants.querySelectorAll("[data-participant-id]").forEach((row) => {
        if (!expectedIds.has(row.dataset.participantId)) {
          row.remove();
        }
      });
    };

    const renderTrack = () => {
      if (!snapshot || !track) {
        return;
      }

      const expectedIds = new Set();
      const ranked = rankedParticipants();
      const visible = visibleParticipantWindow(ranked);
      visible.forEach((participant) => {
        expectedIds.add(participant.profileId);
        const lane = track.querySelector(`[data-track-participant-id="${participant.profileId}"]`) || trackLane(participant);
        updateTrackLane(lane, participant);
        track.insertBefore(lane, windowNote || null);
      });

      track.querySelectorAll("[data-track-participant-id]").forEach((lane) => {
        if (!expectedIds.has(lane.dataset.trackParticipantId)) {
          lane.remove();
        }
      });
    };

    const renderHud = () => {
      if (!snapshot || !hud) {
        return;
      }

      const current = snapshot.participants?.find((participant) => participant.profileId === currentProfileId);
      setText(hud.querySelector("[data-hud-rank]"), current ? String(rankFor(current.profileId)) : "-");
      if (showLiveWpm) {
        setText(hud.querySelector("[data-hud-wpm]"), formatNumber(current?.wpm));
      }

      setText(hud.querySelector("[data-hud-accuracy]"), `${formatNumber(current?.accuracy)} %`);
      setText(hud.querySelector("[data-hud-progress]"), `${Math.round(progressPercent(current))} %`);

      if (current) {
        announceRankChange(rankFor(current.profileId));
      }
    };

    const renderPodium = () => {
      if (!snapshot || !podium) {
        return;
      }

      const terminal = rankedParticipants()
        .filter((participant) => ["Finished", "Dnf"].includes(participant.status))
        .slice(0, 3);
      podium.classList.toggle("is-hidden", !snapshot.finished && terminal.length === 0);
      podium.replaceChildren(element("h2", "Podium"), ...terminal.map((participant) => {
        const row = document.createElement("div");
        row.className = "podium-row";
        row.dataset.podiumParticipantId = participant.profileId;
        const title = document.createElement("strong");
        title.textContent = `${participant.placement || "-"} . ${participant.displayName}`;
        const detail = document.createElement("span");
        detail.textContent = participant.status === "Dnf"
          ? "Nicht beendet"
          : `${formatNumber(participant.wpm)} WPM · ${formatNumber(participant.accuracy)} %`;
        row.append(title, detail);
        return row;
      }));
    };

    const renderReaction = (next) => {
      if (!reactionsEnabled || !reactionStream) {
        return;
      }

      const reaction = camelize(next);
      const chip = document.createElement("span");
      chip.className = "reaction-chip";
      if (reducedMotion) {
        chip.classList.add("static");
      }

      const suffix = reaction.suppressedCount > 0 ? ` +${reaction.suppressedCount}` : "";
      chip.textContent = `${reaction.displayName}: ${reaction.label}${suffix}`;
      reactionStream.prepend(chip);
      while (reactionStream.children.length > 4) {
        reactionStream.lastElementChild?.remove();
      }

      if (!reducedMotion) {
        window.setTimeout(() => chip.classList.add("fading"), 4500);
      }

      window.setTimeout(() => chip.remove(), 6000);
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
      renderPhaseFeedback();
      renderTarget();
      renderParticipants();
      renderTrack();
      renderHud();
      renderPodium();
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
      renderTrack();
      renderHud();
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

    reactionPanel?.addEventListener("click", async (event) => {
      const button = event.target.closest("[data-reaction-key]");
      if (!button || !reactionPanel.contains(button)) {
        return;
      }

      button.disabled = true;
      try {
        await connection.invoke("SendReaction", [roomId, button.dataset.reactionKey]);
      } catch (error) {
        showConnectionError(error);
      } finally {
        window.setTimeout(() => {
          button.disabled = false;
        }, 500);
      }
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

    document.addEventListener("pointerdown", unlockAudio, { once: true, capture: true });
    document.addEventListener("keydown", unlockAudio, { once: true, capture: true });
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
    connection.on("reactionReceived", renderReaction);
    connection.onReconnect(async () => {
      try {
        setText(connectionQuality, "Verbindung: neu verbunden");
        applySnapshot(await connection.invoke("JoinRoom", [roomId]));
      } catch (error) {
        setText(connectionQuality, "Verbindung: Fehler");
        showConnectionError(error);
      }
    });

    connection.start()
      .then(() => {
        setText(connectionQuality, "Verbindung: aktiv");
        return connection.invoke("JoinRoom", [roomId]);
      })
      .then(applySnapshot)
      .catch((error) => {
        setText(connectionQuality, "Verbindung: Fehler");
        showConnectionError(error);
      });

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

function badge(text) {
  const span = document.createElement("span");
  span.className = "badge";
  span.textContent = text;
  return span;
}

function element(tagName, text) {
  const node = document.createElement(tagName);
  node.textContent = text;
  return node;
}

function setText(node, text) {
  if (node) {
    node.textContent = text;
  }
}

function setHidden(node, hidden) {
  if (node) {
    node.classList.toggle("is-hidden", hidden);
  }
}

function initials(value) {
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

function formatNumber(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "-";
  }

  return new Intl.NumberFormat("de-DE", {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1
  }).format(value);
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
