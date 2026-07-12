# Backup und Restore

## Live-Backup

Ein Backup darf bei laufendem Webprozess erstellt werden. SQLite erzeugt dabei einen konsistenten Online-Snapshot:

```bash
docker exec keywars dotnet KeyWars.dll maintenance backup
```

Unter `/data/backups` entstehen zwei zusammengehörige Dateien:

- `keywars-<UTC-Zeit>-<Kennung>.db`
- `keywars-<UTC-Zeit>-<Kennung>.db.manifest.json`

Das Manifest enthält SHA256, Dateigröße, KeyWars-Version, erwartete und angewendete EF-Migrationen sowie den UTC-Erstellungszeitpunkt. Datenbank und Manifest müssen immer gemeinsam aufbewahrt und kopiert werden.

## Restore

Ein Restore ist ausschließlich aus `/data/backups` möglich und erfordert einen gestoppten Webprozess. Der exklusive Runtime-Lock verhindert einen Restore gegen eine laufende KeyWars-Instanz.

```bash
docker compose stop keywars
docker compose run --rm --no-deps keywars maintenance restore /data/backups/keywars-YYYYMMDD-HHMMSS-fff-KENNUNG.db
docker compose up -d keywars
```

Vor dem Austausch prüft KeyWars Manifest, SHA256, Dateigröße, SQLite-Integrität und Migrationsstand. Das Backup wird zunächst als Staging-Datenbank im selben Verzeichnis wie `keywars.db` angelegt. Ist bereits eine aktive Datenbank vorhanden, wird zusätzlich ein `keywars-pre-restore-*.db` samt Manifest erzeugt. Unter dem exklusiven Runtime-Lock muss anschließend ein `wal_checkpoint(TRUNCATE)` der aktiven Datenbank vollständig erfolgreich sein. Erst danach werden WAL-, SHM- und Journal-Sidecars behandelt und die Datenbank atomar ausgetauscht. Schlägt der Checkpoint oder der Austausch fehl, bleibt der bisherige logische Datenbestand aktiv.

Nach dem Start muss die Bereitschaft geprüft werden:

```bash
curl --fail http://127.0.0.1:8080/health/ready
```

Alternativ kann bei gestopptem Container das vollständige Docker-Volume gesichert werden.
