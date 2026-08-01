export class SignalRConnection {
  constructor(path) {
    if (!window.signalR) {
      throw new Error("Der lokale SignalR-Client wurde nicht geladen.");
    }

    this.reconnectingHandlers = [];
    this.reconnectHandlers = [];
    this.disconnectedHandlers = [];
    this.connection = new window.signalR.HubConnectionBuilder()
      .withUrl(path)
      .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
      .configureLogging(window.signalR.LogLevel.Warning)
      .build();
    this.connection.serverTimeoutInMilliseconds = 30000;
    this.connection.keepAliveIntervalInMilliseconds = 10000;
    this.connection.onreconnecting((error) => {
      this.reconnectingHandlers.forEach((handler) => handler(error));
    });
    this.connection.onreconnected((connectionId) => {
      this.reconnectHandlers.forEach((handler) => handler(connectionId));
    });
    this.connection.onclose((error) => {
      this.disconnectedHandlers.forEach((handler) => handler(error));
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
