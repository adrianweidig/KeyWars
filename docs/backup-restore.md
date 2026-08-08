# Backup und Restore

KeyWars erstellt konsistente SQLite-Online-Backups. Ein Backup besteht immer
aus Datenbank **und** Manifest; beide Dateien müssen gemeinsam außerhalb des
Docker-Volumes gesichert werden. Es gibt keinen eingebauten Zeitplan.

## Backup erstellen und exportieren

```bash
docker exec keywars dotnet KeyWars.dll maintenance backup

backup_path="$(docker exec keywars sh -c 'ls -1t /data/backups/keywars-*.db | head -n 1')"
test -n "$backup_path"
mkdir -p keywars-backup-export
docker cp "keywars:$backup_path" keywars-backup-export/
docker cp "keywars:$backup_path.manifest.json" keywars-backup-export/
ls -l keywars-backup-export
```

Das Manifest enthält SHA256, Größe, KeyWars-Version und EF-Migrationen. Das
Verzeichnis anschließend auf unabhängigen Speicher kopieren. Vor jedem Upgrade
und nach wesentlichen Konfigurationsänderungen erneut sichern.

## Restore aus dem bestehenden Volume

Den Webcontainer stoppen; der Restore wird gegen eine laufende Instanz
abgelehnt:

```bash
docker compose stop keywars
backup_path="$(docker compose run --rm --no-deps --entrypoint sh keywars -c 'ls -1t /data/backups/keywars-*.db | head -n 1')"
test -n "$backup_path"
docker compose run --rm --no-deps keywars maintenance restore "$backup_path"
docker compose up -d keywars
curl --fail http://127.0.0.1:8080/health/ready
```

## Exportiertes Backup in ein neues Volume importieren

Das Exportverzeichnis darf genau das gewünschte Dateipaar enthalten:

```bash
backup_file="$(find keywars-backup-export -maxdepth 1 -name 'keywars-*.db' -printf '%f\n' | sort | tail -n 1)"
test -n "$backup_file"
test -f "keywars-backup-export/$backup_file.manifest.json"

docker compose run --rm --no-deps \
  -e BACKUP_FILE="$backup_file" \
  -v "$PWD/keywars-backup-export:/import:ro" \
  --entrypoint sh keywars \
  -c 'mkdir -p /data/backups && cp "/import/$BACKUP_FILE" "/data/backups/$BACKUP_FILE" && cp "/import/$BACKUP_FILE.manifest.json" "/data/backups/$BACKUP_FILE.manifest.json"'

docker compose run --rm --no-deps keywars maintenance restore "/data/backups/$backup_file"
docker compose up -d keywars
curl --fail http://127.0.0.1:8080/health/ready
```

Vor dem Austausch validiert KeyWars Manifest, Hash, Größe, SQLite-Integrität
und Migrationsstand. Eine vorhandene Datenbank wird zusätzlich als
`keywars-pre-restore-*.db` samt Manifest gesichert. Auch dieses Paar muss bei
Bedarf aus dem Volume exportiert werden.
