# Fehlerbehebung

Zuerst Zustand und Logs sichern:

```bash
docker compose ps
docker compose logs --tail 200 keywars
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
```

| Symptom | Prüfen | Maßnahme |
| --- | --- | --- |
| Container startet nicht | Meldung zu LDAP-URL, Base-DN oder UPN-Suffix | `.env` vervollständigen und `docker compose --env-file .env config` ausführen |
| CA-Datei fehlt oder ist ungültig | `KEYWARS_LDAP_CA_CERTIFICATE_PATH` und Logs | CA [vor dem Start in das Volume kopieren](ldap.md#eigene-ca-vor-dem-start-hinterlegen) |
| Login schlägt fehl, Health ist grün | DNS, TCP 636/389, Zertifikatsname, Base-DN und User-Base-DN | echten Login testen; `/health/ready` prüft LDAP nicht |
| Proxy liefert 502/504 | lokales `/health/ready` und Proxy-Upstream | Proxy auf `127.0.0.1:8080` richten oder Netzführung gezielt korrigieren |
| Arena verbindet nicht oder trennt sich | Browser-Netzwerk und `/hubs/arena` | HTTP/1.1-Upgrade, `Upgrade`/`Connection`, Pufferung und Idle-Timeout am Proxy prüfen |
| Falsches Schema, Redirects oder Cookies | `X-Forwarded-Proto` und Proxy-Vertrauen | exakte Proxy-IP oder enges CIDR-Netz konfigurieren |
| SQLite-/Schreibfehler | Volume-Berechtigungen und freier Speicher | `/data` für Containerbenutzer schreibbar machen; vor Eingriff Backup exportieren |
| Arena verliert Progress-Deltas | `/health/arena-progress` | Teilnehmerzahl und Broadcast-Rate messen; Kapazitäten nur schrittweise erhöhen |
| Arena-Abschlüsse bleiben offen | `/health/arena-persistence` | Queue-Fehler und SQLite-Latenz prüfen; Abschlussqueue muss mindestens der Raumzahl entsprechen |
| Offline-Start meldet „image not found“ | `docker image ls` und `RELEASE_MANIFEST.json` | Archiv erneut laden und exakt den ausgelieferten Image-Tag in `.env` verwenden |

Bei Supportanfragen Version, Commit/Release, bereinigte Compose-Konfiguration,
Health-Antworten und relevante Logzeilen mitsenden. Keine Passwörter,
Zertifikatschlüssel oder vollständigen Benutzerdaten weitergeben.
