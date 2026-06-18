# ADR 0010: Begrenzte Channels

## Kontext

Viele Fortschrittsmeldungen dürfen nicht unbeschränkt Speicher aufbauen.

## Entscheidung

Fortschrittskommandos laufen über begrenzte Channels.

## Konsequenzen

Überlast erzeugt kontrollierten Verlust nichtkritischer Progress-Deltas, nicht Datenbanklast.
