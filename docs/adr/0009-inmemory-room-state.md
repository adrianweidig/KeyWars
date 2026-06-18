# ADR 0009: In-memory Raumzustand

## Kontext

Tastenereignisse dürfen SQLite nicht belasten.

## Entscheidung

Aktive Live-Räume liegen im Arbeitsspeicher. SQLite erhält nur zusammengefasste Ergebnisse.

## Konsequenzen

Ein Prozessneustart bricht laufende Räume ab.
