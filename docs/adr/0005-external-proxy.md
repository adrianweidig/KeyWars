# ADR 0005: Externer Reverse Proxy

## Kontext

TLS, DNS und Proxybetrieb sind Betreiberaufgaben.

## Entscheidung

KeyWars lauscht nur per HTTP auf Port 8080.

## Konsequenzen

HTTPS-Weiterleitung, HSTS und WebSocket-Weiterleitung bleiben extern.
