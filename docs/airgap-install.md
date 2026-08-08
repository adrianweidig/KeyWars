# Air-Gap-Installation

Übertrage das vollständige Release-Verzeichnis. Es enthält:

- `keywars-v*-linux-amd64.tar.gz` – Docker-Image für Linux/AMD64;
- `compose.yaml` und `default.env.example`;
- `AIRGAP_INSTALL.md` und `RELEASE_NOTES.md`;
- `RELEASE_MANIFEST.json` mit der exakten Image-Referenz;
- `SHA256SUMS` für alle ausgelieferten Dateien.

## 1. Transfer prüfen

Im Release-Verzeichnis:

```bash
sha256sum -c SHA256SUMS
image_archive="$(find . -maxdepth 1 -name 'keywars-v*-linux-amd64.tar.gz' -print -quit)"
test -n "$image_archive"
gzip -t "$image_archive"
```

Nur ein vollständig geprüftes Verzeichnis auf das Zielsystem übertragen.

## 2. Image und Konfiguration vorbereiten

```bash
docker load -i "$image_archive"
cp default.env.example .env
```

Die ausgelieferte `.env` verweist auf den versionierten Release-Tag. Nicht auf
`latest` umstellen. Mindestens diese Werte eintragen:

- `KEYWARS_LDAP_URLS`: ein oder mehrere `ldaps://`-Ziele;
- `KEYWARS_LDAP_BASE_DN`: Verzeichniswurzel;
- `KEYWARS_LDAP_UPN_SUFFIX`: Domänen-Suffix für kurze Anmeldenamen;
- optional `KEYWARS_LDAP_USER_BASE_DN` als engere Benutzersuchwurzel.

Eine eigene LDAP-CA vor dem ersten Start als `ad-root-ca.pem` neben Compose
ablegen und in dasselbe Compose-Projektvolume kopieren:

```bash
docker compose --project-name keywars --env-file .env run --rm --no-deps \
  --entrypoint sh \
  -v "$PWD/ad-root-ca.pem:/import/ad-root-ca.pem:ro" \
  keywars -c 'mkdir -p /data/certs && cp /import/ad-root-ca.pem /data/certs/ad-root-ca.pem && chmod 0444 /data/certs/ad-root-ca.pem'
```

Danach in `.env` setzen:

```env
KEYWARS_LDAP_CA_CERTIFICATE_PATH=/data/certs/ad-root-ca.pem
```

## 3. Start und Prüfung

```bash
docker compose --project-name keywars --env-file .env config
docker compose --project-name keywars --env-file .env up -d
docker compose --project-name keywars --env-file .env ps
curl --fail http://127.0.0.1:8080/health/ready
```

Anschließend einen echten LDAP-Login über den vorgeschalteten HTTPS-Proxy und
eine Live-Arena mit zwei getrennten Browsersitzungen prüfen. `/health/ready`
prüft SQLite, nicht LDAP.

## Backup, Upgrade und Rollback

Vor jedem Upgrade ein Online-Backup erzeugen und Datenbank plus Manifest aus
dem Volume exportieren:

```bash
docker exec keywars dotnet KeyWars.dll maintenance backup
backup_path="$(docker exec keywars sh -c 'ls -1t /data/backups/keywars-*.db | head -n 1')"
test -n "$backup_path"
mkdir -p keywars-backup-export
docker cp "keywars:$backup_path" keywars-backup-export/
docker cp "keywars:$backup_path.manifest.json" keywars-backup-export/
```

Für das Upgrade neues Release und Prüfsummen verifizieren, Image laden und
eigene Werte kontrolliert in die neue `.env` übertragen. Für ein Rollback den
Container stoppen und das zum alten Release gehörende Backup wiederherstellen.
Eine von einer neueren Version migrierte Datenbank nie ungeprüft mit einem
älteren Image starten.
