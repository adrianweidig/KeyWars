# Backup und Disaster Recovery

Die Sicherung hängt vom Betriebsmodus ab:

- Einzelinstanz: SQLite-Datenbank und Manifest gemeinsam exportieren.
- Scale-Modus: PostgreSQL ist die fachlich führende Datenbank. Redis enthält
  Laufzeitzustand und Data-Protection-Schlüssel.

Backups immer verschlüsselt auf unabhängigen Speicher übertragen und einen
Restore regelmäßig in einer isolierten Umgebung testen.

## Einzelinstanz mit SQLite

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

## Scale-Modus mit PostgreSQL

`pg_dump` in einem zur Server-Hauptversion passenden Client erzeugt ein
portables Custom-Format. Beispiel für Scale-Compose:

```bash
mkdir -p keywars-dr
docker compose --env-file .env.scale -f compose.scale.yaml exec -T postgres \
  pg_dump -U keywars -d keywars --format=custom > "keywars-dr/postgres-$(date -u +%Y%m%dT%H%M%SZ).dump"
```

Im mitgelieferten Swarm-Stack auf einem Manager:

```bash
mkdir -p keywars-dr
pg_container="$(docker ps --filter label=com.docker.swarm.service.name=keywars_keywars-postgres -q | head -n 1)"
test -n "$pg_container"
docker exec "$pg_container" pg_dump -U keywars -d keywars --format=custom \
  > "keywars-dr/postgres-$(date -u +%Y%m%dT%H%M%SZ).dump"
```

Bei externem PostgreSQL den Backupdienst des Anbieters und zusätzlich
regelmäßige logische Dumps nutzen. Aufbewahrung, Verschlüsselung, PITR und
regionsgetrennte Kopien liegen beim Betreiber.

### PostgreSQL wiederherstellen

1. Edge, Web, Arena und Worker stoppen; in Kubernetes vorher HPAs entfernen.
2. Ziel und Backup-Zeitpunkt nochmals prüfen.
3. In eine leere Datenbank oder kontrolliert mit `--clean --if-exists`
   wiederherstellen.
4. Die zur Image-Version gehörende Rolle `migrate` erfolgreich ausführen.
5. Anwendung starten und `/health/ready`, Anmeldung, Tippversuch und Arena
   prüfen.

Swarm-Beispiel bei bereits gestoppten Anwendungsdiensten:

```bash
pg_container="$(docker ps --filter label=com.docker.swarm.service.name=keywars_keywars-postgres -q | head -n 1)"
test -n "$pg_container"
test -f keywars-dr/postgres-restore.dump
docker exec -i "$pg_container" pg_restore -U keywars -d keywars \
  --clean --if-exists --no-owner < keywars-dr/postgres-restore.dump
```

Ein Datenbankschema nicht isoliert zurückrollen. Bei inkompatibler Migration
immer passendes Datenbank-Backup und vorherige Image-Version gemeinsam nutzen.

## Redis

Redis ist nicht die führende Ablage für Ergebnisse, enthält aber aktive
Sitzungen, SignalR-/Laufzeitzustand und Data-Protection-Schlüssel. Ein Verlust
bricht laufende Arenen ab und macht bestehende Anmelde-Cookies ungültig; neue
Anmeldungen und PostgreSQL-Daten bleiben möglich.

Der Swarm-Stack aktiviert AOF. Vor einem Storage-Snapshot `redis-cli SAVE`
ausführen und danach das **gesamte** Redis-Volume einschließlich AOF sichern.
Bei Managed Redis die Backup-/Restore-Funktion des Anbieters verwenden. Ein
einzelnes `dump.rdb` ersetzt bei aktivem AOF keinen Volume-Snapshot.

Redis nur bei gestoppten Anwendungsdiensten wiederherstellen und exakt dieselbe
oder eine kompatible Redis-Hauptversion verwenden. Ist Sitzungsfortbestand
nicht erforderlich, ist ein bewusst leer gestartetes Redis sicherer als ein
unklarer oder nur teilweise kopierter Datenbestand.

## Vollständiger DR-Satz

Außer den Daten gehören in die verschlüsselte Betriebsablage:

- verwendeter Image-Tag und nach Möglichkeit Digest;
- Compose-/Swarm-/Kubernetes-Manifeste;
- nicht geheime Site-Konfiguration und LDAP-CA-Zertifikate;
- TLS-Proxy- beziehungsweise Ingress-Konfiguration;
- Wiederanlaufreihenfolge und getestete RTO/RPO-Werte;
- Referenzen auf Secrets im Secret Manager, nicht deren Klartext im Repo.

Wiederanlauf: PostgreSQL und Redis bereitstellen, Daten wiederherstellen,
Migration ausführen, Worker/Arena/Web starten, Edge zuletzt freigeben. Danach
fachliche Tests durchführen; ein grüner Healthcheck allein ist kein
Restore-Nachweis.
