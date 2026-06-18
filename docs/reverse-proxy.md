# Reverse Proxy

Der Betreiber stellt extern HTTPS bereit und leitet auf `http://<docker-host>:8080` weiter. WebSocket-Upgrade für `/hubs/arena` muss zugelassen sein.

Erforderlich:

- HTTPS öffentlich;
- HTTP zum Container;
- Host-Header erhalten;
- WebSocket-Idle-Timeout mindestens 3600 Sekunden;
- Request-Body mindestens 256 KiB;
- keine Response-Pufferung für WebSockets;
- HTTP nach HTTPS am Proxy umleiten;
- HSTS optional am Proxy setzen.

KeyWars erzeugt ausschließlich relative interne Links und führt keine HTTPS-Weiterleitung aus.
