import {
  badge,
  camelize,
  connectionStatusText,
  element,
  formatDuration,
  formatNumber,
  initials,
  isExactInput,
  isPendingPersistenceState,
  normalizePersistenceState,
  persistenceStateFor,
  persistenceStatusText,
  phaseLabel,
  podiumTitle,
  setHidden,
  setStatusText,
  setText,
  showConnectionError,
  statusLabel,
  statusPill,
  tableCell,
  teamName,
  textSpan
} from "./arena-view.js";
import { SignalRConnection } from "./signalr-connection.js";
import { renderTypingCharacters, splitGraphemes } from "./typing-text.js";
import { resetTypingScroll, scrollCurrentCharacterIntoView } from "./typing-scroll.js";

const persistencePollDelays = [250, 500, 1000, 2000, 3000, 5000];
const terminalPersistenceStates = new Set(["Persisted", "Failed", "AbortedUnconfirmed"]);

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
    const liveBoard = root.querySelector("[data-arena-live-board]");
    const podium = root.querySelector("[data-arena-podium]");
    const teamBoard = root.querySelector("[data-arena-team-board]");
    const teams = root.querySelector("[data-arena-teams]");
    const roundLabel = document.querySelector("[data-arena-round-label]");
    const participantCountLabel = document.querySelector("[data-arena-participant-count]");
    const liveRegion = root.querySelector("[data-arena-live-region]");
    const modeLabel = root.querySelector("[data-arena-mode-label]");
    const rosterSummaryLabel = root.querySelector("[data-arena-roster-summary]");
    const connectionQuality = root.querySelector("[data-arena-connection-quality]");
    const persistenceStatus = root.querySelector("[data-arena-persistence-status]");
    const hiddenCountLabel = root.querySelector("[data-arena-hidden-count]");
    const windowNote = root.querySelector("[data-arena-window-note]");
    const phaseSteps = [...root.querySelectorAll(".arena-phase-steps li")];
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
    const progressClasses = Array.from({ length: 21 }, (_, index) => `race-progress-${index * 5}`);
    root.dataset.motionReduced = reducedMotion ? "true" : "false";

    let snapshot = null;
    let sequence = 0;
    let progressTimer = 0;
    let timerFrame = 0;
    let startRefreshTimer = 0;
    let timerKey = "";
    let clockOffset = 0;
    let countdownRefreshScheduled = false;
    let backspaces = 0;
    let focusLosses = 0;
    let finishedLocally = false;
    let previousCurrentRank = null;
    let lastRankAnnouncementAt = 0;
    let previousPhase = null;
    let audioUnlocked = false;
    let audioContext = null;
    let readyPending = false;
    let startPending = false;
    let unavailable = false;
    let lastRenderedTargetText = null;
    let connectionStatus = "disconnected";
    let persistencePollTimer = 0;
    let persistencePollAttempt = 0;
    let persistencePollController = null;
    let persistencePollExhausted = false;
    let disposed = false;
    let persistenceState = normalizePersistenceState(root.dataset.persistenceState) || "Inactive";
    let restoreInputFocusAfterReconnect = false;
    let ignoreNextInputBlur = false;

    const setInputDisabled = (disabled) => {
      if (!input) {
        return;
      }

      if (disabled && !input.disabled && document.activeElement === input) {
        ignoreNextInputBlur = true;
      }

      input.disabled = disabled;
      if (ignoreNextInputBlur) {
        window.queueMicrotask(() => {
          ignoreNextInputBlur = false;
        });
      }
    };

    const renderTarget = () => {
      if (!snapshot || !target || !input) {
        return;
      }

      const typed = splitGraphemes(input.value);
      const targetText = snapshot.targetText || "";
      const targetChanged = targetText !== lastRenderedTargetText;
      const expected = splitGraphemes(targetText);
      if (expected.length === 0) {
        target.replaceChildren(textSpan("Der Text wird zum Start freigegeben.", "muted"));
        resetTypingScroll(target);
        lastRenderedTargetText = targetText;
        return;
      }

      renderTypingCharacters(target, expected, (char, index) => {
        if (index < typed.length) {
          return typed[index] === char ? "correct" : "wrong";
        }

        return index === typed.length ? "current" : "";
      });
      if (targetChanged) {
        resetTypingScroll(target);
      }

      scrollCurrentCharacterIntoView(target);
      lastRenderedTargetText = targetText;
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

    const updateClockOffset = (serverNow) => {
      if (!serverNow) {
        return;
      }

      const serverTime = new Date(serverNow).getTime();
      if (Number.isFinite(serverTime)) {
        clockOffset = serverTime - Date.now();
      }
    };

    const progressClass = (percent) => {
      const bucket = Math.round(Math.max(0, Math.min(100, percent)) / 5) * 5;
      return `race-progress-${bucket}`;
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
          ? total === 1 ? `1 aktive Person von ${capacity} Plätzen` : `${total} aktive Teilnehmende von ${capacity} Plätzen`
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
        liveBoard?.classList.toggle(name, mode === name);
        participantList?.classList.toggle(name, mode === name);
      });
      setText(modeLabel, modeTitle(mode));
      setText(rosterSummaryLabel, rosterSummary(ranked.length, visible.length));
      setText(participantCountLabel, `${ranked.length} ${ranked.length === 1 ? "Person" : "Personen"}`);
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
        tableCell(""),
        tableCell(document.createElement("span")),
        tableCell(""),
        tableCell(""),
        tableCell("")
      );
      return row;
    };

    const updateParticipantRow = (row, participant) => {
      ["Name", "Team", "Status", "Fortschritt", "Punkte", "Platz"].forEach((label, index) => {
        row.cells[index].dataset.label = label;
      });
      row.cells[0].textContent = participant.displayName;
      row.cells[1].textContent = teamName(participant.teamNumber);
      row.cells[2].replaceChildren(statusPill(participant.status));
      row.cells[3].textContent = `${participant.correctCharacters} / ${snapshot.targetCharacterCount}`;
      row.cells[4].textContent = String(participant.seriesPoints || 0);
      row.cells[5].textContent = participant.placement ? String(participant.placement) : participant.rankHint ? `~${participant.rankHint}` : String(rankFor(participant.profileId));
    };

    const renderTeams = () => {
      if (!teamBoard || !teams || !snapshot) {
        return;
      }

      const teamStandings = Array.isArray(snapshot.teams) ? snapshot.teams : [];
      teamBoard.classList.toggle("is-hidden", snapshot.mode !== "Team");
      teams.replaceChildren(...teamStandings.map((team) => {
        const row = document.createElement("div");
        row.className = "arena-team-row";
        row.dataset.teamNumber = String(team.teamNumber);
        const title = document.createElement("strong");
        title.textContent = `${team.placement || "-"}. ${team.name}`;
        const detail = document.createElement("span");
        detail.textContent = `${team.points} Punkte · ${team.roundWins} ${team.roundWins === 1 ? "Rundensieg" : "Rundensiege"}`;
        row.append(title, detail);
        return row;
      }));
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
      position.className = "race-position race-progress-0";
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
        position.classList.remove(...progressClasses);
        position.classList.add(progressClass(percent));
        position.querySelector("span").textContent = initials(participant.displayName);
      }
    };

    const liveTypingRow = (participant) => {
      const row = document.createElement("article");
      row.className = "live-typing-row";
      row.dataset.liveParticipantId = participant.profileId;
      const meta = document.createElement("div");
      meta.className = "live-typing-meta";
      const preview = document.createElement("div");
      preview.className = "live-typing-preview";
      preview.dataset.livePreview = "";
      preview.tabIndex = 0;
      preview.setAttribute("role", "region");
      row.append(meta, preview);
      return row;
    };

    const updateLiveTypingRow = (row, participant) => {
      const meta = row.querySelector(".live-typing-meta");
      const preview = row.querySelector("[data-live-preview]");
      const percent = Math.round(progressPercent(participant));
      row.classList.toggle("current", participant.profileId === currentProfileId);
      row.dataset.liveParticipantId = participant.profileId;
      if (meta) {
        const token = element("span", initials(participant.displayName));
        token.className = "race-token";
        const identity = document.createElement("div");
        const name = document.createElement("strong");
        name.textContent = participant.displayName;
        const detail = document.createElement("span");
        detail.textContent = `${statusLabel(participant.status)} · ${percent} %`;
        identity.append(name, detail);
        const children = [token, identity];
        if (participant.profileId === snapshot.creatorProfileId) {
          children.push(badge("Host"));
        }

        meta.replaceChildren(...children);
      }

      if (preview) {
        preview.tabIndex = 0;
        preview.setAttribute("role", "region");
        preview.setAttribute("aria-label", `${participant.displayName}: Live-Textfortschritt`);
        renderTypingPreview(preview, participant);
      }
    };

    const renderTypingPreview = (container, participant) => {
      const expected = splitGraphemes(snapshot?.targetText || "");
      const states = String(participant?.typedTextPreview || "");
      if (expected.length === 0) {
        container.replaceChildren(textSpan("Der Text wird zum Start freigegeben.", "muted"));
        resetTypingScroll(container);
        return;
      }

      const typedLength = Math.min(states.length, expected.length);
      renderTypingCharacters(container, expected, (char, index) => {
        const state = states[index];
        if (state === "c") {
          return "correct";
        } else if (state === "w") {
          return "wrong";
        } else if (index === typedLength && participant?.status === "Running") {
          return "current";
        }

        return "pending";
      });
      scrollCurrentCharacterIntoView(container);
    };

    const triggerRankBoost = () => {
      const lane = track?.querySelector(`[data-track-participant-id="${currentProfileId}"]`);
      if (!lane) {
        return;
      }

      lane.classList.remove("rank-boost");
      void lane.offsetWidth;
      lane.classList.add("rank-boost");
      window.setTimeout(() => lane.classList.remove("rank-boost"), 450);
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
          triggerRankBoost();
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

    const renderLiveTypingBoard = (changedParticipantIds = null) => {
      if (!snapshot || !liveBoard) {
        return;
      }

      const expectedIds = new Set();
      const ranked = rankedParticipants();
      const visible = visibleParticipantWindow(ranked);
      renderRosterMode(ranked, visible);
      visible.forEach((participant) => {
        expectedIds.add(participant.profileId);
        const existingRow = liveBoard.querySelector(`[data-live-participant-id="${participant.profileId}"]`);
        const row = existingRow || liveTypingRow(participant);
        if (!existingRow || changedParticipantIds === null || changedParticipantIds.has(participant.profileId)) {
          updateLiveTypingRow(row, participant);
        }

        liveBoard.append(row);
      });

      liveBoard.querySelectorAll("[data-live-participant-id]").forEach((row) => {
        if (!expectedIds.has(row.dataset.liveParticipantId)) {
          row.remove();
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
      const persistenceState = persistenceStateFor(snapshot);
      podium.classList.toggle("is-hidden", !snapshot.finished && terminal.length === 0);
      podium.dataset.persistenceState = persistenceState;
      podium.replaceChildren(element("h2", podiumTitle(persistenceState)), ...terminal.map((participant) => {
        const row = document.createElement("div");
        row.className = "podium-row";
        row.dataset.podiumParticipantId = participant.profileId;
        const title = document.createElement("strong");
        title.textContent = `${participant.placement || "-"} . ${participant.displayName}`;
        const detail = document.createElement("span");
        const performance = participant.status === "Dnf"
          ? "Nicht beendet"
          : `${formatNumber(participant.wpm)} WPM · ${formatNumber(participant.accuracy)} %`;
        detail.textContent = `${participant.seriesPoints || 0} Punkte · ${performance}`;
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
      const connected = connectionStatus === "connected";
      const readyButton = readyForm?.querySelector("button");
      const startButton = startForm?.querySelector("button");
      if (!snapshot) {
        setInputDisabled(true);
        if (readyButton) {
          readyButton.disabled = true;
        }

        if (startButton) {
          startButton.disabled = true;
        }

        if (dnfButton) {
          dnfButton.disabled = true;
        }

        if (leaveButton) {
          leaveButton.disabled = true;
        }

        reactionPanel?.querySelectorAll("button").forEach((button) => {
          button.disabled = true;
        });
        return;
      }

      const running = snapshot.phase === "Running";
      const lobby = snapshot.phase === "Lobby";
      const betweenRounds = snapshot.phase === "RoundResults" && !snapshot.finished;
      renderPhaseSteps();
      if (state) {
        state.textContent = phaseLabel(snapshot.phase);
      }

      setInputDisabled(!connected || !running || finishedLocally || !snapshot.targetText);

      if (dnfButton) {
        dnfButton.disabled = !connected || !running || finishedLocally;
        setHidden(dnfButton, !running);
      }

      const current = snapshot.participants?.find((participant) => participant.profileId === currentProfileId);
      const canStart = (lobby || betweenRounds) && snapshot.creatorProfileId === currentProfileId;
      setHidden(readyForm, !lobby);
      if (readyButton) {
        readyButton.textContent = readyPending ? "Wird gespeichert..." : current?.ready ? "Nicht bereit" : "Bereit";
        readyButton.disabled = !connected || readyPending || !snapshot || !lobby;
      }

      setHidden(startForm, !canStart);
      if (startButton) {
        startButton.textContent = startPending ? "Startet..." : betweenRounds ? "Nächste Runde" : "Starten";
        startButton.disabled = !connected || startPending || !snapshot || !canStart;
      }

      if (leaveButton) {
        leaveButton.disabled = !connected;
      }

      reactionPanel?.querySelectorAll("button").forEach((button) => {
        button.disabled = !connected;
      });

      renderTimer();
    };

    const renderPhaseSteps = () => {
      if (phaseSteps.length === 0 || !snapshot) {
        return;
      }

      const order = ["Lobby", "Countdown", "Running", "Results"];
      const currentIndex = snapshot.finished || ["RoundResults", "SeriesResults", "Closed"].includes(snapshot.phase)
        ? 3
        : Math.max(0, order.indexOf(snapshot.phase));
      phaseSteps.forEach((step, index) => {
        step.classList.toggle("active", index === currentIndex);
        step.classList.toggle("done", index < currentIndex);
      });
    };

    const renderTimer = () => {
      if (!timer || !snapshot) {
        return;
      }

      const value = timer.querySelector("strong");
      const label = timer.querySelector("span");
      if (!value || !label) {
        return;
      }

      const nextTimerKey = [
        snapshot.phase,
        snapshot.raceStartsAt || "",
        snapshot.startedAt || "",
        snapshot.roundEndsAt || "",
        snapshot.finishedAt || "",
        snapshot.finished ? "finished" : "active"
      ].join("|");
      if (nextTimerKey === timerKey) {
        return;
      }

      timerKey = nextTimerKey;
      countdownRefreshScheduled = false;
      cancelAnimationFrame(timerFrame);

      if (snapshot.phase === "Countdown" && snapshot.raceStartsAt) {
        const raceStartsAt = new Date(snapshot.raceStartsAt).getTime();
        const tick = () => {
          const remaining = Math.max(0, raceStartsAt - (Date.now() + clockOffset));
          value.textContent = remaining <= 0 ? "LOS" : Math.ceil(remaining / 1000).toString();
          label.textContent = "Countdown";
          if (remaining <= 0) {
            if (!countdownRefreshScheduled) {
              countdownRefreshScheduled = true;
              window.clearTimeout(startRefreshTimer);
              startRefreshTimer = window.setTimeout(() => {
                connection.invoke("JoinRoom", [roomId]).then(applySnapshot).catch(showConnectionError);
              }, 80);
            }

            return;
          }

          timerFrame = requestAnimationFrame(tick);
        };
        tick();
        return;
      }

      const roundStartedAtValue = snapshot.raceStartsAt || snapshot.startedAt;
      if (!roundStartedAtValue) {
        value.textContent = snapshot.phase === "Lobby" ? "Bereit" : "-";
        label.textContent = snapshot.phase === "Lobby" ? "Lobby" : phaseLabel(snapshot.phase);
        return;
      }

      const startedAt = new Date(roundStartedAtValue).getTime();
      const tick = () => {
        const endedAt = snapshot.roundEndsAt || snapshot.finishedAt;
        const end = endedAt ? new Date(endedAt).getTime() : Date.now() + clockOffset;
        value.textContent = formatDuration(Math.max(0, end - startedAt));
        label.textContent = snapshot.phase === "RoundResults" ? "Rundendauer" : snapshot.finished ? "Dauer" : "vergangen";
        if (!endedAt) {
          timerFrame = requestAnimationFrame(tick);
        }
      };
      tick();
    };

    const setConnectionStatus = (nextStatus) => {
      connectionStatus = nextStatus;
      root.dataset.connectionState = nextStatus;
      setStatusText(connectionQuality, connectionStatusText(nextStatus));
      renderState();
    };

    const stopPersistencePolling = () => {
      window.clearTimeout(persistencePollTimer);
      persistencePollTimer = 0;
      persistencePollController?.abort();
      persistencePollController = null;
    };

    const currentPersistenceState = () => snapshot ? persistenceStateFor(snapshot) : persistenceState;

    const renderPersistenceStatus = (schedulePoll = true) => {
      if (snapshot) {
        persistenceState = persistenceStateFor(snapshot);
      }

      root.dataset.persistenceState = persistenceState;
      root.dataset.persistencePollExhausted = persistencePollExhausted ? "true" : "false";
      if (persistenceStatus) {
        setHidden(persistenceStatus, persistenceState === "Inactive");
        setStatusText(persistenceStatus, persistenceStatusText(persistenceState, persistencePollExhausted));
      }

      if (terminalPersistenceStates.has(persistenceState)) {
        stopPersistencePolling();
      } else if (schedulePoll && isPendingPersistenceState(persistenceState)) {
        schedulePersistencePoll();
      }
    };

    const pollPersistenceStatus = async () => {
      if (disposed || unavailable || !isPendingPersistenceState(currentPersistenceState())) {
        return;
      }

      persistencePollAttempt += 1;
      persistencePollController = new AbortController();
      const requestController = persistencePollController;
      const requestTimeout = window.setTimeout(() => requestController.abort(), 4000);
      try {
        const response = await window.fetch(`/api/arena/${encodeURIComponent(roomId)}/speicherstatus`, {
          credentials: "same-origin",
          headers: { Accept: "application/json" },
          signal: requestController.signal
        });
        if (!response.ok) {
          throw new Error(`Speicherstatus nicht verfügbar (${response.status}).`);
        }

        const payload = camelize(await response.json());
        const nextState = normalizePersistenceState(payload.state);
        const currentState = currentPersistenceState();
        if (nextState && !terminalPersistenceStates.has(currentState)) {
          persistenceState = nextState === "FinishedPending" ? "Pending" : nextState;
          if (snapshot?.finished) {
            snapshot.persistenceState = persistenceState;
          }
        }
      } catch (error) {
        if (error?.name !== "AbortError") {
          console.warn("Arena-Speicherstatus konnte nicht abgefragt werden.", error);
        }
      } finally {
        window.clearTimeout(requestTimeout);
        if (persistencePollController === requestController) {
          persistencePollController = null;
        }
      }

      renderPodium();
      renderPersistenceStatus(false);
      if (!disposed && isPendingPersistenceState(currentPersistenceState())) {
        schedulePersistencePoll();
      }
    };

    const schedulePersistencePoll = () => {
      if (disposed || unavailable || persistencePollTimer || persistencePollController ||
          !isPendingPersistenceState(currentPersistenceState())) {
        return;
      }

      if (persistencePollAttempt >= persistencePollDelays.length) {
        persistencePollExhausted = true;
        renderPersistenceStatus(false);
        return;
      }

      const delay = persistencePollDelays[persistencePollAttempt];
      persistencePollTimer = window.setTimeout(() => {
        persistencePollTimer = 0;
        void pollPersistenceStatus();
      }, delay);
    };

    const applySnapshot = (next) => {
      if (!next || unavailable) {
        return;
      }

      const previousPersistenceState = currentPersistenceState();
      const incoming = camelize(next);
      if (incoming.finished && terminalPersistenceStates.has(previousPersistenceState) &&
          !terminalPersistenceStates.has(persistenceStateFor(incoming))) {
        incoming.persistenceState = previousPersistenceState;
      }

      const roundChanged = snapshot && Number(snapshot.currentRound) !== Number(incoming.currentRound);
      snapshot = incoming;
      if (roundChanged) {
        sequence = 0;
        backspaces = 0;
        focusLosses = 0;
        finishedLocally = false;
        previousCurrentRank = null;
        lastRenderedTargetText = null;
        if (input) {
          input.value = "";
        }
      }
      persistenceState = persistenceStateFor(snapshot);
      const currentParticipant = snapshot.participants?.find((participant) => participant.profileId === currentProfileId);
      const serverSequence = Number(currentParticipant?.sequence);
      if (Number.isSafeInteger(serverSequence) && serverSequence >= 0) {
        sequence = Math.max(sequence, serverSequence);
      }

      updateClockOffset(snapshot.serverNow);
      renderPhaseFeedback();
      renderTarget();
      renderLiveTypingBoard();
      renderParticipants();
      renderTrack();
      renderHud();
      renderTeams();
      renderPodium();
      setText(roundLabel, `Runde ${snapshot.currentRound} von ${snapshot.roundCount}`);
      renderState();
      renderPersistenceStatus();
    };

    const handleRoomUnavailable = (message) => {
      if (unavailable) {
        return;
      }

      unavailable = true;
      clearTimeout(progressTimer);
      progressTimer = 0;
      clearTimeout(startRefreshTimer);
      stopPersistencePolling();
      cancelAnimationFrame(timerFrame);
      setInputDisabled(true);

      readyForm?.querySelector("button")?.setAttribute("disabled", "disabled");
      startForm?.querySelector("button")?.setAttribute("disabled", "disabled");
      dnfButton?.setAttribute("disabled", "disabled");
      leaveButton?.setAttribute("disabled", "disabled");
      connectionStatus = "disconnected";
      root.dataset.connectionState = connectionStatus;
      setStatusText(connectionQuality, "Verbindung: Raum nicht verfügbar");
      showConnectionError(new Error(message || "Der Live-Raum wurde nicht gefunden."));
      window.setTimeout(() => {
        window.location.href = "/arena";
      }, 2500);
    };

    const applyProgressBatch = (next) => {
      const batch = camelize(next);
      if (!snapshot || batch.roomId !== snapshot.roomId || !Array.isArray(batch.deltas)) {
        return;
      }

      if (batch.roomVersion < snapshot.roundVersion) {
        return;
      }

      updateClockOffset(batch.serverNow);
      snapshot.roundVersion = Math.max(snapshot.roundVersion, batch.roomVersion);
      const changedParticipantIds = new Set();
      batch.deltas.forEach((delta) => {
        const participant = snapshot.participants?.find((item) => item.profileId === delta.participantId);
        if (!participant) {
          return;
        }

        participant.correctCharacters = delta.correctCharacters;
        participant.typedTextPreview = delta.typedTextPreview || "";
        participant.wpm = delta.wpm;
        participant.accuracy = delta.accuracy;
        participant.rankHint = delta.rankHint;
        changedParticipantIds.add(participant.profileId);
      });
      renderLiveTypingBoard(changedParticipantIds);
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
      setInputDisabled(true);

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
      setInputDisabled(true);

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
      if (readyPending) {
        return;
      }

      readyPending = true;
      renderState();
      try {
        const current = snapshot?.participants?.find((participant) => participant.profileId === currentProfileId);
        applySnapshot(await connection.invoke("SetReady", [roomId, current?.ready !== true]));
      } catch (error) {
        showConnectionError(error);
      } finally {
        readyPending = false;
        renderState();
      }
    });

    startForm?.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (startPending) {
        return;
      }

      startPending = true;
      renderState();
      try {
        applySnapshot(await connection.invoke("Start", [roomId]));
      } catch (error) {
        showConnectionError(error);
      } finally {
        startPending = false;
        renderState();
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
    input?.addEventListener("blur", () => {
      if (ignoreNextInputBlur) {
        ignoreNextInputBlur = false;
        return;
      }

      focusLosses += 1;
    });
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
    connection.on("roomUnavailable", handleRoomUnavailable);
    connection.onReconnecting(() => {
      window.clearTimeout(progressTimer);
      progressTimer = 0;
      restoreInputFocusAfterReconnect = restoreInputFocusAfterReconnect || document.activeElement === input;
      setConnectionStatus("reconnecting");
    });
    connection.onReconnect(async () => {
      try {
        applySnapshot(await connection.invoke("JoinRoom", [roomId]));
        setConnectionStatus("connected");
        if (restoreInputFocusAfterReconnect && input && !input.disabled) {
          input.focus({ preventScroll: true });
        }

        restoreInputFocusAfterReconnect = false;
      } catch (error) {
        restoreInputFocusAfterReconnect = false;
        setConnectionStatus("disconnected");
        showConnectionError(error);
      }
    });
    connection.onDisconnected((error) => {
      if (disposed || unavailable) {
        return;
      }

      restoreInputFocusAfterReconnect = false;
      setConnectionStatus("disconnected");
      if (error) {
        showConnectionError(error);
      }
    });

    renderState();
    renderPersistenceStatus();

    connection.start()
      .then(() => {
        setConnectionStatus("connected");
        return connection.invoke("JoinRoom", [roomId]);
      })
      .then(applySnapshot)
      .catch((error) => {
        setConnectionStatus("disconnected");
        showConnectionError(error);
      });

    window.addEventListener("pagehide", () => {
      disposed = true;
      stopPersistencePolling();
      if (connection.isConnected()) {
        connection.invoke("LeaveRoom", [roomId]).catch(() => {});
      }
    });
  });
}
