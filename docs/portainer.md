# Portainer

## Stack in etwa zehn Minuten bereitstellen

1. `compose.yaml` und `.env.example` aus demselben Release laden.
2. `.env.example` lokal als `.env` speichern.
3. Image-Tag unverändert versioniert lassen und LDAP-Werte eintragen.
4. In Portainer **Stacks → Add stack** öffnen und den Stack exakt `keywars` nennen.
5. `compose.yaml` einfügen oder hochladen und die Werte aus `.env` über **Load variables from .env file** laden.
6. Falls eine eigene LDAP-CA nötig ist, das Volume vor dem Deployment wie unter [LDAP](ldap.md#eigene-ca-vor-dem-start-hinterlegen) mit `docker compose --project-name keywars` initialisieren.
7. Stack deployen und Containerzustand sowie Logs prüfen.

Der feste Stack-/Projektname ist wichtig: Docker präfixiert das Volume mit dem
Compose-Projektnamen. Ein anderer Name würde neben dem vorbereiteten Volume ein
zweites, leeres Volume erzeugen.

Der Standard veröffentlicht Port `8080` nur auf `127.0.0.1`. Das passt zu
einem HTTPS-Proxy auf demselben Docker-Host. Läuft der Proxy in einem anderen
Container oder auf einem anderen Host, muss die Netzführung bewusst angepasst
und durch Firewallregeln begrenzt werden; nicht pauschal auf allen Interfaces
veröffentlichen.

## Abnahme

- Container ist `healthy`;
- `/health/ready` antwortet über den Proxy;
- WebSocket-Verbindungen zu `/hubs/arena` funktionieren;
- ein echter LDAP-Login gelingt;
- das vom Stack `keywars` erzeugte Datenvolume ist vorhanden und wird extern gesichert.

## Update

1. [Backup](backup-restore.md) erstellen und Dateipaar exportieren.
2. Neues Release und Prüfsummen verifizieren.
3. Exakten neuen `KEYWARS_VERSION`-Tag setzen; nicht `latest` verwenden.
4. Stack aktualisieren und Image neu abrufen.
5. Health, Login und eine Live-Arena prüfen.

Portainer ersetzt weder Backup-Export noch TLS-Termination. Der Stack enthält
keinen Reverse Proxy und keinen automatischen Backup-Zeitplan.
