# Live-Arena

Live-Raeume unterstuetzen zwei bis n Personen. Raumzustaende liegen im
Arbeitsspeicher, Eingaben werden als Fortschrittsbatches verarbeitet und nicht
pro Taste in SQLite geschrieben. Ein Host-Start fuehrt zuerst in eine
serverseitige Countdown-Phase; der Zieltext wird erst zur freigegebenen
Startzeit im Snapshot ausgeliefert.

Grenzen:

- `KEYWARS__LIVE__MAX_PARTICIPANTS_PER_ROOM`
- `KEYWARS__LIVE__MAX_SPECTATORS_PER_ROOM`
- `KEYWARS__LIVE__MAX_CONCURRENT_ROOMS`
- `KEYWARS__LIVE__PROGRESS_BROADCAST_HZ`
- `KEYWARS__LIVE__COUNTDOWN_SECONDS`
- `KEYWARS__LIVE__RECONNECT_GRACE_SECONDS`

Bei erreichter Kapazität werden neue Räume oder Beitritte kontrolliert abgelehnt.
