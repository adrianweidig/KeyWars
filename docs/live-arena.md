# Live-Arena

Live-Räume unterstützen zwei bis n Personen. Raumzustände liegen im Arbeitsspeicher, Eingaben werden als Fortschrittsbatches verarbeitet und nicht pro Taste in SQLite geschrieben.

Grenzen:

- `KEYWARS__LIVE__MAX_PARTICIPANTS_PER_ROOM`
- `KEYWARS__LIVE__MAX_SPECTATORS_PER_ROOM`
- `KEYWARS__LIVE__MAX_CONCURRENT_ROOMS`
- `KEYWARS__LIVE__PROGRESS_BROADCAST_HZ`
- `KEYWARS__LIVE__RECONNECT_GRACE_SECONDS`

Bei erreichter Kapazität werden neue Räume oder Beitritte kontrolliert abgelehnt.
