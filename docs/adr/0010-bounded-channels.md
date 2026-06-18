# ADR 0010: Geplante begrenzte Channels

## Kontext

Viele Fortschrittsmeldungen dürfen nicht unbeschränkt Speicher aufbauen.

## Entscheidung

Fortschrittskommandos sollen ueber begrenzte Channels laufen. Der aktuelle
Produktionspfad verarbeitet Progressmeldungen noch direkt im `LiveRoomManager`.
Die Channel-Architektur ist deshalb ein verbindlicher Zielzustand fuer KW-015,
aber kein bereits abgeschlossener Ist-Zustand.

## Konsequenzen

Bis KW-015 abgeschlossen ist, duerfen Dokumentation und UI keine belastbare
Backpressure-Garantie behaupten. Nach Umsetzung soll Ueberlast kontrollierten
Verlust nichtkritischer Progress-Deltas erzeugen, nicht Datenbanklast.
