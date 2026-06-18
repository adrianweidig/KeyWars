# ADR 0001: Ein Container und SQLite

## Kontext

Der Betrieb soll mit genau einem Container und einem persistenten Volume möglich sein.

## Entscheidung

KeyWars nutzt ASP.NET Core und SQLite im selben Container.

## Konsequenzen

Keine zusätzlichen Laufzeitdienste, einfache Sicherung, aber keine horizontale Skalierung.

## Verworfene Alternativen

PostgreSQL, Redis, Message Broker und getrennte Frontends.
