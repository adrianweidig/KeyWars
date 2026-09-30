# LDAP und Active Directory

KeyWars bindet direkt mit Benutzername und Passwort. Es gibt kein Servicekonto;
das Passwort wird weder gespeichert noch geloggt. Direkte `memberOf`-Werte
steuern optional die Content-Moderation, aber nicht den allgemeinen Zugang.

## Erforderliche `.env`-Werte

| Variable | Inhalt |
| --- | --- |
| `KEYWARS_LDAP_URLS` | ein oder mehrere `ldaps://`-Ziele, getrennt durch Semikolon |
| `KEYWARS_LDAP_BASE_DN` | Verzeichniswurzel für die Benutzersuche |
| `KEYWARS_LDAP_UPN_SUFFIX` | Suffix für kurze Anmeldenamen |

Optional:

| Variable | Standard | Verwendung |
| --- | --- | --- |
| `KEYWARS_LDAP_USER_BASE_DN` | leer | engere Suchwurzel für Benutzer; sonst Base-DN |
| `KEYWARS_LDAP_CA_CERTIFICATE_PATH` | leer | CA-Datei im Container, üblicherweise `/data/certs/ad-root-ca.pem` |
| `KEYWARS_LDAP_CONNECT_TIMEOUT_SECONDS` | 5 | Verbindungs-/Bind-Timeout, 1 bis 60 Sekunden |
| `KEYWARS_LDAP_OPERATION_TIMEOUT_SECONDS` | 10 | Such-Timeout, 1 bis 120 Sekunden |
| `KEYWARS_LDAP_ALLOW_STARTTLS` | `false` | aktiviert StartTLS für `ldap://`-Ziele |
| `KEYWARS_LDAP_ALLOW_PLAINTEXT` | `false` | erlaubt ausdrücklich `ldap://` ohne TLS, wenn StartTLS ausgeschaltet ist |

Moderationsgruppen werden außerhalb von Development ausschließlich als
vollständige `memberOf`-DNs über `KEYWARS_MODERATOR_GROUP_DNS` konfiguriert.
`KEYWARS_MODERATOR_GROUP_VALUES` ist nur für lokale Entwicklung zulässig.
Leere Werte vergeben keine Rechte. Details:
[Konfiguration](configuration.md#content-moderation).

LDAPS ist der Standard. Bei einer eigenen CA müssen die LDAP-DNS-Namen zum
Zertifikat passen. Ohne eigenen CA-Pfad gilt der Zertifikatsspeicher des
Containers.

## LDAP ohne TLS

Für ein Verzeichnis ohne LDAPS und StartTLS `KEYWARS_LDAP_URLS` auf die echten
`ldap://`-Server mit Port `389` setzen und `KEYWARS_LDAP_ALLOW_PLAINTEXT=true`
sowie `KEYWARS_LDAP_ALLOW_STARTTLS=false` konfigurieren. Der direkte Bind
überträgt Benutzername und Passwort dann unverschlüsselt. Beschränke die
Verbindung auf das dafür vorgesehene interne Netz. Die AD-Richtlinien müssen
diesen Bind zulassen; erzwungene LDAP-Signierung kann ihn ablehnen.

Eine aktivierte StartTLS-Option hat Vorrang und fällt bei einem TLS-Fehler
nicht auf Klartext zurück. Die Browseranmeldung bleibt hinter HTTPS.

## Eigene CA vor dem Start hinterlegen

Die PEM-kodierte CA-Datei neben `compose.yaml` unter dem Namen
`ad-root-ca.pem` ablegen. `.env` zuerst vollständig ausfüllen. Verwende für
CLI-Initialisierung und Portainer denselben Compose-Projektnamen `keywars`,
damit beide dasselbe Volume verwenden:

```bash
docker compose --project-name keywars --env-file .env run --rm --no-deps \
  --entrypoint sh \
  -v "$PWD/ad-root-ca.pem:/import/ad-root-ca.pem:ro" \
  keywars -c 'mkdir -p /data/certs && cp /import/ad-root-ca.pem /data/certs/ad-root-ca.pem && chmod 0444 /data/certs/ad-root-ca.pem'

docker compose --project-name keywars --env-file .env run --rm --no-deps \
  --entrypoint sh keywars \
  -c 'test -r /data/certs/ad-root-ca.pem'
```

Danach setzen:

```env
KEYWARS_LDAP_CA_CERTIFICATE_PATH=/data/certs/ad-root-ca.pem
```

Erst jetzt `docker compose --project-name keywars --env-file .env up -d`
ausführen.

## Abnahme

1. `docker compose --project-name keywars --env-file .env config` endet ohne fehlende Variablen.
2. `docker compose --project-name keywars --env-file .env logs keywars` zeigt keinen Zertifikats- oder Startfehler.
3. Ein echter Login mit kurzem Namen und – falls zugelassen – UPN gelingt.
4. Ein kontrolliert nicht erreichbares zweites LDAP-Ziel bestätigt das Failover.

`/health/ready` prüft LDAP absichtlich nicht. Bei Verzeichnisausfall bleibt die
Anwendung bereit, neue Logins schlagen jedoch fehl.
