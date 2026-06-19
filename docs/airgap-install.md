# Air-Gap-Installation

Ein KeyWars-Release enthaelt ein echtes Docker-Imagearchiv im Format
`docker save | gzip`, die Compose-Datei, `.env.example`, `SHA256SUMS` und
`RELEASE_MANIFEST.json`. Das Zielsystem benoetigt fuer den Import keine
Internetverbindung.

1. Release-Artefakte auf einem Internetrechner laden:
   `keywars-vX.Y.Z-linux-amd64.tar.gz`, `compose.yaml`, `.env.example`,
   `RELEASE_MANIFEST.json` und `SHA256SUMS`.
2. Pruefsummen auf dem Internetrechner oder Transferhost pruefen:

```bash
sha256sum -c SHA256SUMS
gzip -t keywars-vX.Y.Z-linux-amd64.tar.gz
```

1. Alle geprueften Artefakte sicher auf das Zielsystem uebertragen.
2. Auf dem Zielsystem importieren:

```bash
docker load -i keywars-vX.Y.Z-linux-amd64.tar.gz
```

1. Den in `RELEASE_MANIFEST.json` dokumentierten versionierten Tag oder Digest
   in `.env` oder Portainer setzen.
2. LDAP-/LDAPS-Variablen in der Zielumgebung setzen.
3. Stack starten:

```bash
docker compose --env-file .env up -d
docker compose logs -f keywars
```

Vor Upgrades immer `/data` sichern. Fuer Rollback den alten versionierten Tag
eintragen und dieselbe Daten-Sicherung verwenden.

Nach Installation benötigt KeyWars nur Browser/Proxy-Zugriffe und LDAP/LDAPS zu Domain Controllern.
