# Konfiguration

KeyWars liest Konfiguration aus `KEYWARS`-Abschnitten. In Umgebungsvariablen
wird der Doppelunterstrich verwendet, zum Beispiel
`KEYWARS__LDAP__BASE_DN`.

## LDAP

| Variable | Standard | Verbraucher | Regel |
| --- | --- | --- | --- |
| `KEYWARS__LDAP__URLS` | leer | `LdapAuthenticator` | außerhalb Development erforderlich |
| `KEYWARS__LDAP__BASE_DN` | leer | `LdapAuthenticator` | außerhalb Development erforderlich |
| `KEYWARS__LDAP__UPN_SUFFIX` | leer | `LdapAuthenticator` | außerhalb Development erforderlich |
| `KEYWARS__LDAP__NETBIOS_DOMAIN` | leer | `LdapAuthenticator` | optional |
| `KEYWARS__LDAP__USER_BASE_DN` | leer | `LdapAuthenticator` | optional |
| `KEYWARS__LDAP__CA_CERTIFICATE_PATH` | leer | `LdapAuthenticator`, `StartupValidator` | optionaler Root-CA-Pfad, Datei muss existieren |
| `KEYWARS__LDAP__CONNECT_TIMEOUT_SECONDS` | `5` | `LdapAuthenticator`, `StartupValidator` | 1 bis 60 Sekunden |
| `KEYWARS__LDAP__OPERATION_TIMEOUT_SECONDS` | `10` | `LdapAuthenticator`, `StartupValidator` | 1 bis 120 Sekunden |
| `KEYWARS__LDAP__ALLOW_STARTTLS` | `false` | `StartupValidator` | für `ldap://` außerhalb Development erforderlich |

## Auth

| Variable | Standard | Verbraucher | Regel |
| --- | --- | --- | --- |
| `KEYWARS__AUTH__COOKIE_LIFETIME_HOURS` | `8` | Cookie-Auth | 1 bis 12 Stunden |
| `KEYWARS__AUTH__DEVELOPMENT_LOGIN` | `false` | `StartupValidator` | außerhalb Development verboten |

In der Umgebung `Development` wird der lokale Test-Login aktiv. In allen
anderen Umgebungen wird ausschließlich LDAP/LDAPS verwendet.

## Live-Arena

| Variable | Standard | Verbraucher | Regel |
| --- | --- | --- | --- |
| `KEYWARS__LIVE__MAX_PARTICIPANTS_PER_ROOM` | `64` | `LiveRoomManager` | mindestens 2 |
| `KEYWARS__LIVE__MAX_SPECTATORS_PER_ROOM` | `128` | geplant KW-027 | noch nicht verfügbar |
| `KEYWARS__LIVE__MAX_CONCURRENT_ROOMS` | `200` | `LiveRoomManager` | mindestens 1 |
| `KEYWARS__LIVE__MAX_CONNECTIONS_PER_USER` | `3` | `LivePresenceTracker` | 1 bis 20 aktive Arena-Verbindungen pro Profil |
| `KEYWARS__LIVE__PROGRESS_BROADCAST_HZ` | `10` | `LiveProgressBroadcaster` | maximaler Progress-Broadcast-Takt je Raum |
| `KEYWARS__LIVE__COUNTDOWN_SECONDS` | `3` | `LiveRoomManager` | 1 bis 10 Sekunden |
| `KEYWARS__LIVE__RECONNECT_GRACE_SECONDS` | `30` | `LiveRoomManager` | 0 bis 300 Sekunden |
| `KEYWARS__LIVE__ROOM_COMMAND_QUEUE_CAPACITY` | `4096` | `LiveProgressBroadcaster` | Pending-Kapazität für koaleszierte Progress-Deltas |
| `KEYWARS__LIVE__COMPLETION_QUEUE_CAPACITY` | `4096` | `LiveRoomCompletionQueue` | begrenzte Queue für Arena-Abschlussjobs |
| `KEYWARS__LIVE__COMPLETED_ROOM_RETENTION_MINUTES` | `60` | `LiveRoomManager` | Cleanup-Retention |
| `KEYWARS__LIVE__LOBBY_ROOM_RETENTION_MINUTES` | `720` | `LiveRoomManager` | Cleanup-Retention |

## Inhalte und Challenges

| Variable | Standard | Verbraucher | Regel |
| --- | --- | --- | --- |
| `KEYWARS__CONTENT__MAX_UPLOAD_BYTES` | `131072` | `TextLibraryService` | Importlimit |
| `KEYWARS__CONTENT__MAX_TEXT_CHARACTERS` | `20000` | `TextLibraryService` | maximale UTF-16-Zeichen nach NFC-Normalisierung |
| `KEYWARS__CONTENT__MAX_TEXT_GRAPHEMES` | `20000` | `TextLibraryService` | maximale Grapheme nach NFC-Normalisierung |
| `KEYWARS__CONTENT__MAX_TEXT_LINES` | `400` | `TextLibraryService` | maximale Zeilenanzahl |
| `KEYWARS__CHALLENGES__MAX_PARTICIPANTS` | `64` | `ChallengeService` | mindestens 2 |

## Daten

| Variable | Standard | Verbraucher | Regel |
| --- | --- | --- | --- |
| `KEYWARS__DATA__DIRECTORY` | `/data` in Production | `DataPaths` | muss schreibbar sein |

SQLite liegt unter `KEYWARS__DATA__DIRECTORY/keywars.db`.
Data-Protection-Schlüssel und Backups liegen ebenfalls unter diesem
Verzeichnis.

## Schutzgrenzen

KeyWars setzt feste Rate-Limits ohne Zusatzkonfiguration:

| Bereich | Limit | Schlüssel |
| --- | --- | --- |
| Login | 10 POST-Versuche pro Minute | Remote-IP |
| API | 180 Requests pro Minute | Profil-ID, sonst Remote-IP |

Der Content-Security-Policy-Header erlaubt Scripts und Styles nur von `self`.
SignalR-Verbindungen werden auf `self` und den aktuellen WebSocket-Host
beschränkt.

## Diagnosen

| Pfad | Zweck |
| --- | --- |
| `/health/live` | Prozess lebt |
| `/health/ready` | SQLite ist erreichbar |
| `/health/arena-persistence` | Pending Jobs, Kapazität und Fehlversuche der Arena-Abschlussqueue |
| `/health/arena-progress` | aktive Progress-Räume, Pending Deltas, Koaleszierungen, Drops und Broadcastzähler |
