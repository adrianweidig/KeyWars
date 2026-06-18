# ADR 0015: Keine vollständigen Keystroke-Replays

## Kontext

Vollständige Eingabereplays erhöhen Datenschutz- und Speicheraufwand.

## Entscheidung

KeyWars speichert zusammengefasste Metriken, aber keine vollständigen Keystroke-Replays.

## Konsequenzen

Analysen nutzen aggregierte Schwächenbeobachtungen, Wortdauer-Samples und per
Alignment abgeleitete Fehlerpositionen. Diese Daten enthalten keine vollständige
zeitliche Tastenfolge und reichen nicht aus, eine Eingabesession als Replay
nachzubilden.
