# ADR 0011: Vertikale Skalierung

## Kontext

Das Zielmodell verbietet horizontale Skalierung.

## Entscheidung

KeyWars skaliert innerhalb einer Instanz vertikal.

## Konsequenzen

Es gibt keine Backplane, keinen Cluster und genau einen aktiven Prozess pro Volume.
