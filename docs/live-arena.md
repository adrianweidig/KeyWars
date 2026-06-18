# Live-Arena

Live-Raeume unterstuetzen zwei bis n Personen. Raumzustaende liegen im
Arbeitsspeicher, Eingaben werden als Fortschrittsbatches verarbeitet und nicht
pro Taste in SQLite geschrieben. Ein Host-Start fuehrt zuerst in eine
serverseitige Countdown-Phase; der Zieltext wird erst zur freigegebenen
Startzeit im Snapshot ausgeliefert.

Bis die Serienlogik aus KW-018 vollstaendig umgesetzt ist, akzeptiert der
produktive Raumvertrag genau eine Runde. Anfragen fuer 3- oder 5-Runden-Serien
werden kontrolliert abgelehnt, damit die UI keine Best-of-Serie verspricht, die
nach Runde 1 endet.

Praesenz wird pro Profil und aktiver SignalR-Connection gefuehrt. Mehrere Tabs
derselben Person erzeugen eine Teilnehmerzeile. Erst wenn die letzte
Raumverbindung eines Profils verschwindet, startet die Reconnect-Grace. Ein
periodischer Hintergrund-Sweep setzt abgelaufene Lobby-Verbindungen auf
`Vor dem Start verlassen` und abgelaufene Rennverbindungen auf `Nicht beendet`.
Verlaesst die Raumleitung in der Lobby den Raum, geht die Leitung auf die
aelteste aktive Person ueber.

Beendete Arena-Runden werden nicht per Fire-and-forget gespeichert. Der
Raummanager erstellt einen unveraenderlichen Abschlussrecord mit Raum-ID, Runde,
Raumversion und Idempotenzschluessel. Eine begrenzte gehostete Queue schreibt
die Zusammenfassung und alle Teilnehmerresultate in einer SQLite-Transaktion,
berechnet das Arena-Rating genau einmal und aktualisiert Profilrating,
Matchanzahl und Saisonpunkte atomar. Transiente SQLite-Fehler werden begrenzt
mit Backoff wiederholt; dauerhaft fehlgeschlagene Jobs bleiben in der
Queue-Diagnose sichtbar.

Beim Anwendungs-Shutdown werden laufende Countdown- und Rennraeume als
`AbortedByServer` abgeschlossen. Diese Abbrueche werden nachvollziehbar
persistiert, veraendern aber kein Rating. Lobby-Raeume werden weiterhin nur als
fluechtiger Arbeitsspeicherzustand verworfen.

Hochfrequenter Schreibfortschritt wird als kompaktes `progressChanged`-Batch
uebertragen: Raumversion, Profil-ID, Fortschritt, WPM, Genauigkeit und
Ranghinweis. Der Broadcast-Takt ist durch
`KEYWARS__LIVE__PROGRESS_BROADCAST_HZ` begrenzt. Innerhalb eines Takts wird nur
das neueste Delta je Person behalten; bei voller Pending-Kapazitaet werden neue
nichtkritische Progress-Deltas verworfen und unter `/health/arena-progress`
sichtbar. Zuverlaessige Ereignisse wie Start, Finish, Leave und Phasenwechsel
nutzen weiterhin Vollsnapshots.

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

Bei erreichter Kapazität werden neue Räume oder Beitritte kontrolliert abgelehnt.
