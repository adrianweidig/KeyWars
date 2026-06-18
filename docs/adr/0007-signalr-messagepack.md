# ADR 0007: SignalR und MessagePack

## Kontext

Live-Räume benötigen bidirektionale Verbindungen.

## Entscheidung

Die Arena stellt einen SignalR-Hub bereit und aktiviert MessagePack zusätzlich zum JSON-Protokoll.

## Konsequenzen

Der externe Proxy muss WebSockets für `/hubs/arena` zulassen.
