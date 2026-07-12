function fakeSignalRSource() {
  return String.raw`
(() => {
  class FakeConnection {
    constructor() {
      this.state = "Disconnected";
      this.handlers = new Map();
      this.reconnectingHandlers = [];
      this.reconnectedHandlers = [];
      this.closeHandlers = [];
      this.invocations = [];
    }

    on(target, handler) {
      this.handlers.set(target, handler);
    }

    onreconnecting(handler) {
      this.reconnectingHandlers.push(handler);
    }

    onreconnected(handler) {
      this.reconnectedHandlers.push(handler);
    }

    onclose(handler) {
      this.closeHandlers.push(handler);
    }

    async start() {
      if (new URLSearchParams(window.location.search).get("signalRStart") === "failed") {
        throw new Error("Test-Verbindung konnte nicht gestartet werden.");
      }

      this.state = "Connected";
    }

    async invoke(target, ...args) {
      this.invocations.push({ target, args });
      return this.snapshot();
    }

    snapshot() {
      const root = document.querySelector("[data-arena-room]");
      const profileId = root.dataset.currentProfileId;
      const running = new URLSearchParams(window.location.search).get("arenaTest") === "running";
      const targetText = "Frontendstatus und Wiederverbindung werden deterministisch geprüft. Die Arena behält Fokus, Sequenz und sichtbaren Fortschritt auch nach einer kurzen Unterbrechung bei. Ein längerer Zieltext macht die Tastaturnavigation in der scrollbaren Region zuverlässig prüfbar. Teilnehmende können den aktuellen Abschnitt lesen, ohne dass die gesamte Seite ihre Position verändert. Danach bleibt genug Inhalt übrig, um auch am mobilen Breakpoint vertikal zu scrollen.";
      const now = Date.now();
      return {
        roomId: root.dataset.roomId,
        creatorProfileId: profileId,
        code: "TEST01",
        title: "Arena Persistenztest",
        targetText,
        targetCharacterCount: Array.from(targetText).length,
        maxParticipants: 2,
        mode: "Classic",
        visibility: "Private",
        roundCount: 1,
        currentRound: 1,
        roundVersion: 1,
        phase: running ? "Running" : "SeriesResults",
        started: true,
        finished: !running,
        serverNow: new Date(now).toISOString(),
        phaseChangedAt: new Date(now - 1000).toISOString(),
        countdownStartsAt: null,
        raceStartsAt: new Date(now - 10000).toISOString(),
        startedAt: new Date(now - 9000).toISOString(),
        finishedAt: running ? null : new Date(now - 500).toISOString(),
        closeReason: null,
        participants: [{
          profileId,
          displayName: "Frontend Test",
          status: running ? "Running" : "Finished",
          ready: true,
          sequence: 7,
          correctCharacters: running ? 4 : Array.from(targetText).length,
          typedTextPreview: running ? "cccc" : "c".repeat(Array.from(targetText).length),
          wpm: 42.5,
          placement: running ? null : 1,
          durationMilliseconds: running ? null : 8500,
          accuracy: 99.5
        }],
        persistenceState: running ? null : "Pending"
      };
    }

    emitReconnecting() {
      this.state = "Reconnecting";
      this.reconnectingHandlers.forEach((handler) => handler(new Error("Test-Unterbrechung")));
    }

    emitReconnected() {
      this.state = "Connected";
      this.reconnectedHandlers.forEach((handler) => handler("fake-connection"));
    }

    emitClosed() {
      this.state = "Disconnected";
      this.closeHandlers.forEach((handler) => handler());
    }
  }

  class FakeHubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() {
      const params = new URLSearchParams(window.location.search);
      if (params.get("initialPersistence") === "pending") {
        document.querySelector("[data-arena-room]").dataset.persistenceState = "Pending";
      }

      const connection = new FakeConnection();
      window.__arenaFakeConnection = connection;
      return connection;
    }
  }

  window.signalR = {
    HubConnectionBuilder: FakeHubConnectionBuilder,
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connected: "Connected",
      Reconnecting: "Reconnecting"
    },
    LogLevel: { Warning: 3 }
  };
})();`;
}

module.exports = { fakeSignalRSource };
