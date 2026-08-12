export class SignalRConnection {
  constructor(path) {
    if (!window.signalR) {
      throw new Error("Der lokale SignalR-Client wurde nicht geladen.");
    }

    this.reconnectingHandlers = [];
    this.reconnectHandlers = [];
    this.disconnectedHandlers = [];
    this.retryDelays = [2000, 5000, 10000, 30000];
    this.retryAttempt = 0;
    this.retryTimer = 0;
    this.disposed = false;
    this.connection = new window.signalR.HubConnectionBuilder()
      .withUrl(path, {
        transport: window.signalR.HttpTransportType.WebSockets,
        skipNegotiation: true
      })
      .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
      .configureLogging(window.signalR.LogLevel.Warning)
      .build();
    this.connection.serverTimeoutInMilliseconds = 30000;
    this.connection.keepAliveIntervalInMilliseconds = 10000;
    this.connection.onreconnecting((error) => {
      this.clearRetry();
      this.reconnectingHandlers.forEach((handler) => handler(error));
    });
    this.connection.onreconnected((connectionId) => {
      this.retryAttempt = 0;
      this.reconnectHandlers.forEach((handler) => handler(connectionId));
    });
    this.connection.onclose((error) => {
      this.disconnectedHandlers.forEach((handler) => handler(error));
      this.scheduleRetry(error);
    });
  }

  on(target, handler) {
    this.connection.on(target, handler);
  }

  onReconnect(handler) {
    this.reconnectHandlers.push(handler);
  }

  onReconnecting(handler) {
    this.reconnectingHandlers.push(handler);
  }

  onDisconnected(handler) {
    this.disconnectedHandlers.push(handler);
  }

  async start() {
    if (this.connection.state !== window.signalR.HubConnectionState.Disconnected) {
      return;
    }

    try {
      await this.connection.start();
      this.retryAttempt = 0;
    } catch (error) {
      this.scheduleRetry(error);
      throw error;
    }
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

  dispose() {
    this.disposed = true;
    this.clearRetry();
    return this.connection.stop?.() || Promise.resolve();
  }

  clearRetry() {
    if (!this.retryTimer) {
      return;
    }

    window.clearTimeout(this.retryTimer);
    this.retryTimer = 0;
  }

  scheduleRetry(error) {
    if (this.disposed || this.retryTimer ||
        this.connection.state !== window.signalR.HubConnectionState.Disconnected) {
      return;
    }

    const delay = this.retryDelays[Math.min(this.retryAttempt, this.retryDelays.length - 1)];
    this.retryAttempt += 1;
    this.retryTimer = window.setTimeout(async () => {
      this.retryTimer = 0;
      if (this.disposed || this.connection.state !== window.signalR.HubConnectionState.Disconnected) {
        return;
      }

      this.reconnectingHandlers.forEach((handler) => handler(error));
      try {
        await this.connection.start();
        this.retryAttempt = 0;
        this.reconnectHandlers.forEach((handler) => handler(this.connection.connectionId || null));
      } catch (retryError) {
        this.scheduleRetry(retryError);
      }
    }, delay);
  }
}
