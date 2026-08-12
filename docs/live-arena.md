# Live-Arena

Live-Räume unterstützen standardmäßig bis zu 64 Personen. Ihr aktiver
Raumzustand liegt im Arbeitsspeicher der zuständigen Arena-Instanz; im
Scale-Modus sichert Redis zusätzlich Zuständigkeit und Presence. Verfügbar sind
Einzelrennen, Serien über drei oder fünf Runden und eine automatisch
ausgeglichene Teamwertung über eine Runde.

## Zustands- und Persistenzmodell

- Tastenfortschritt bleibt flüchtig und wird nicht pro Taste in die Datenbank geschrieben.
- Progress-Deltas werden pro Person koalesziert und höchstens mit der konfigurierten Broadcast-Rate gesendet.
- Start, Finish, Leave und Phasenwechsel liefern zuverlässige Vollsnapshots.
- Erst das Ende eines Rennens oder einer Serie erzeugt einen idempotenten Abschlussjob mit aggregierten Ergebnissen.
- Rating, XP und Saisonpunkte gelten erst nach Status `Persisted` als bestätigt.
- Bei Server-Shutdown werden laufende Rennen ohne Ratingänderung als abgebrochen gespeichert; Lobbys sind flüchtig.

Mehrere Tabs derselben Person ergeben eine Teilnehmerzeile. Nach Verlust der
letzten Verbindung läuft die Reconnect-Frist. Verlässt die Raumleitung Lobby
oder Serienpause, übernimmt die älteste aktive Person und kann ohne Reload
fortfahren.

Arena-Zieltexte werden vor der Auswahl normalisiert und auf Grapheme sowie
UTF-8-Größe begrenzt. Zu lange Texte bleiben als Trainingsinhalt erhalten, sind
aber nicht als Live-Ziel auswählbar. `KEYWARS_MAX_ARENA_TARGET_GRAPHEMES`
begrenzt den administrativ wählbaren Wert auf höchstens 2800.

## Skalierung

Bis acht Personen zeigt die Strecke alle Details, bis 24 eine kompakte Ansicht.
Ab 25 werden Top-Plätze, eigene Position und direkte Nachbarn priorisiert.
Eine produktive Zuschauerrolle existiert nicht.

Für hohe Last zuerst diese Endpunkte beobachten:

- `/health/arena-progress`: aktive Räume, Pending-Deltas, Koaleszierungen, Drops und Broadcasts;
- `/health/arena-persistence`: Abschlussjobs, Wiederholungen, Fehler und Persistenzdauer.

Drops nicht sofort mit größeren Queues überdecken. Zuerst Teilnehmerzahl,
`KEYWARS_LIVE_BROADCAST_HZ`, CPU, Arbeitsspeicher sowie Datenbank- und
Redis-Latenz messen.
Alle wirksamen Grenzen stehen in der
[Konfigurationsübersicht](configuration.md#live-arena-und-kapazität).

## Proxy- und Abnahmetest

Der externe Proxy muss WebSocket-Upgrades für `/hubs/arena` erlauben. Nach
Installation oder Update mindestens mit mehreren getrennten Browser-Sitzungen
prüfen:

1. Raum erstellen und beitreten;
2. Countdown und Startzeit sind für alle gleich;
3. Fortschritt und Rangfolge erscheinen gegenseitig;
4. Reconnect innerhalb der Frist erhält die Teilnahme;
5. Endstand und Persistenzstatus stimmen in allen Sitzungen überein;
6. Teammitglieder werden ausgeglichen verteilt und dieselbe Teamwertung angezeigt;
7. nach Verbindungsverlust der Raumleitung kann die älteste aktive Person die Serie fortsetzen.
