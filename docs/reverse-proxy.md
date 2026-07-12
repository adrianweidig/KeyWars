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
- vertrauenswürdige Proxy-IP-Adressen oder -Netze gemäß
  [Konfiguration](configuration.md#reverse-proxy) eintragen.

KeyWars erzeugt ausschließlich relative interne Links und führt keine
HTTPS-Weiterleitung aus. Nach einem HTTPS-Scheme, das direkt oder über einen
vertrauenswürdigen `X-Forwarded-Proto`-Header ermittelt wurde, emittiert die
Anwendung HSTS. Der Proxy darf diesen Header zusätzlich setzen oder härten.
