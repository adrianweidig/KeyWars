# Live-Arena

Live-Räume unterstützen zwei bis n Personen. Raumzustände liegen im
Arbeitsspeicher, Eingaben werden als Fortschrittsbatches verarbeitet und nicht
pro Taste in SQLite geschrieben. Ein Host-Start führt zuerst in eine
serverseitige Countdown-Phase; der Zieltext wird erst zur freigegebenen
Startzeit im Snapshot ausgeliefert.

Der produktive Raumvertrag unterstützt drei auswählbare Formate:

- das klassische Einzelrennen über eine Runde;
- das Serienrennen über drei oder fünf Runden;
- die Teamwertung über eine Runde mit automatisch ausgeglichener Verteilung
  auf Team Alpha und Team Bravo.

In einer Serie erhält ein fehlerfrei beendeter Lauf absteigende
Platzierungspunkte; ein nicht beendeter Lauf erhält null Punkte. Die
Gesamtwertung sortiert nach Punkten, Rundensiegen, beendeten Runden,
Gesamtzeit und durchschnittlicher Genauigkeit. In der Teamwertung werden die
Punkte aller Teammitglieder addiert. Bei Gleichstand entscheiden Rundensiege,
beendete Läufe und Gesamtzeit. Diese Regeln werden serverseitig angewendet und
als Teil jedes Raumsnapshots ausgeliefert.

Präsenz wird pro Profil und aktiver SignalR-Connection geführt. Mehrere Tabs
derselben Person erzeugen eine Teilnehmerzeile. Erst wenn die letzte
Raumverbindung eines Profils verschwindet, startet die Reconnect-Grace. Ein
periodischer Hintergrund-Sweep setzt abgelaufene Lobby-Verbindungen auf
`Vor dem Start verlassen` und abgelaufene Rennverbindungen auf `Nicht beendet`.
Verlässt die Raumleitung in der Lobby den Raum, geht die Leitung auf die
älteste aktive Person über.

Beendete Arena-Räume werden nicht per Fire-and-forget gespeichert. Bei einer
Serie bleiben Zwischenergebnisse im flüchtigen Raumzustand; erst die
Gesamtwertung erzeugt genau einen unveränderlichen Abschlussrecord mit Raum-ID, Runde,
Raumversion und Idempotenzschlüssel. Eine begrenzte gehostete Queue schreibt
die Zusammenfassung, Teamzuordnung und alle aggregierten Teilnehmerresultate in einer SQLite-Transaktion,
berechnet das Arena-Rating genau einmal und aktualisiert Profilrating,
Matchanzahl und Saisonpunkte atomar. Transiente SQLite-Fehler werden begrenzt
mit Backoff wiederholt; dauerhaft fehlgeschlagene Jobs bleiben in der
Queue-Diagnose sichtbar.

Der Browser trennt das vorläufige Spielergebnis vom Commitstatus:

- `Pending`: Podium/WPM sind vorläufig; Rating und XP sind noch unbestätigt;
- `Persisted`: Summary, Rating und Rewards sind committed;
- `Failed`: Speicherung ist fehlgeschlagen, Rating und XP wurden nicht vergeben;
- `AbortedUnconfirmed`: nach Prozessverlust existiert weder eine bestätigte
  Summary noch ein wiederaufnehmbarer Raum.

Der authentifizierte Endpunkt `GET /api/arena/{roomId}/speicherstatus` prüft
zuerst die persistierte Summary und danach den begrenzten Queue-Status. Er gibt
nur den Zustand aus, keine Teilnehmenden-, Raumcode- oder Textdaten. Die
anonyme Health-Diagnose `/health/arena-persistence` enthält ausschließlich
niedrig-kardinale Aggregate für Pending/Failed, Retries, Persistenzdauer und
Abbrüche.

Beim Anwendungs-Shutdown werden laufende Countdown- und Rennräume als
`AbortedByServer` abgeschlossen. Diese Abbrüche werden nachvollziehbar
persistiert, verändern aber kein Rating. Lobby-Räume werden weiterhin nur als
flüchtiger Arbeitsspeicherzustand verworfen.

Hochfrequenter Schreibfortschritt wird als kompaktes `progressChanged`-Batch
übertragen: Raumversion, Profil-ID, Fortschritt, transiente Textpreview,
WPM, Genauigkeit und Ranghinweis. Die Textpreview ist kein gespeichertes
Keystroke-Replay, sondern ein flüchtiger Zustandsstring für die sichtbare
10FastFingers-artige Zieltextmarkierung im aktiven Raum. SQLite erhält diese
Preview nicht. Der Broadcast-Takt ist durch
`KEYWARS__LIVE__PROGRESS_BROADCAST_HZ` begrenzt. Innerhalb eines Takts wird nur
das neueste Delta je Person behalten; bei voller Pending-Kapazität werden neue
nichtkritische Progress-Deltas verworfen und unter `/health/arena-progress`
sichtbar. Zuverlässige Ereignisse wie Start, Finish, Leave und Phasenwechsel
nutzen weiterhin Vollsnapshots.

Die kanonische Raumseite rendert dieselben serverbestätigten Daten als
oberen Tippbereich, Live-Textboard, Rennstrecke, persönliches HUD, Rangliste
und Podium-Container. Deltas aktualisieren die Zieltextmarkierungen,
Positionen per CSS-Transform und die textuelle Live-Region gedrosselt;
Reduced-Motion deaktiviert gleitende Positionsübergänge.

Die Darstellung skaliert nach Teilnehmerfeld: bis acht Personen bleibt die
Detailansicht aktiv, neun bis 24 Personen nutzen eine kompaktere Strecke, und
ab 25 Personen rendert die fokussierte Ansicht nur Top-Plätze, eigene Position
und direkte Rangnachbarn. Die UI zeigt dabei aktive Teilnehmende, Raumkapazität
und Verbindungszustand. Eine echte Zuschauerrolle mit eigenen Berechtigungen
und niedriger priorisierten Updates ist noch nicht produktiv implementiert.

Das Motion-Budget nutzt zentrale CSS-Tokens: `instant` 80 ms, `fast` 180 ms,
`normal` 260 ms und `celebration` 520 ms mit festgelegten Easing-Kurven.
Laufende Arena-Bewegung ist auf Transform und Opacity beschränkt. Die
Profiloption `ReducedMotion` und `prefers-reduced-motion` deaktivieren
nichtessenzielle Bewegung. Sounds werden nicht als externe Assets geladen,
sondern lokal per Web Audio erzeugt und erst nach expliziter Nutzerinteraktion
aktiviert; Sound ist standardmäßig aus und besitzt eine separate
Profil-Lautstärke. Reaktionen sind feste Presets ohne freien Chattext und
werden serverseitig pro Profil/Raum begrenzt.

Grenzen:

- `KEYWARS__LIVE__MAX_PARTICIPANTS_PER_ROOM`
- `KEYWARS__LIVE__MAX_SPECTATORS_PER_ROOM`
- `KEYWARS__LIVE__MAX_CONCURRENT_ROOMS`
- `KEYWARS__LIVE__MAX_CONNECTIONS_PER_USER`
- `KEYWARS__LIVE__PROGRESS_BROADCAST_HZ`
- `KEYWARS__LIVE__ROOM_COMMAND_QUEUE_CAPACITY`
- `KEYWARS__LIVE__COUNTDOWN_SECONDS`
- `KEYWARS__LIVE__RECONNECT_GRACE_SECONDS`
- `KEYWARS__LIVE__COMPLETION_QUEUE_CAPACITY`
- `KEYWARS__LIVE__COMPLETION_DRAIN_TIMEOUT_SECONDS`

Bei erreichter Kapazität werden neue Räume oder Beitritte kontrolliert abgelehnt.
