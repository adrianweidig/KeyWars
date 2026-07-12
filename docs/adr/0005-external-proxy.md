# ADR 0005: Externer Reverse Proxy

## Kontext

TLS, DNS und Proxybetrieb sind Betreiberaufgaben.

## Entscheidung

KeyWars lauscht nur per HTTP auf Port 8080. Die Anwendung wertet
`X-Forwarded-Proto` ausschließlich von explizit vertrauenswürdigen
Proxyadressen beziehungsweise -netzen aus und emittiert HSTS, wenn das daraus
ermittelte Request-Scheme HTTPS ist.

## Konsequenzen

TLS-Terminierung, HTTP-zu-HTTPS-Weiterleitung und WebSocket-Weiterleitung
bleiben extern. Der Proxy darf HSTS zusätzlich setzen oder härten; KeyWars setzt
den Header bereits nach einem validierten HTTPS-Scheme.
