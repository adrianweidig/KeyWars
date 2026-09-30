# Konfiguration

Im Repository heißt die vollständige Compose-Vorlage `.env.example`; im
Release wird derselbe Inhalt als `default.env.example` veröffentlicht:

```bash
cp .env.example .env
docker compose --env-file .env config
```

`compose.yaml` übersetzt die kurzen `.env`-Namen in die internen
`KEYWARS__...`-Variablen. Änderungen werden erst nach einem Neustart des
betroffenen Dienstes wirksam.

## Betriebsart

Ohne weitere Auswahl läuft KeyWars als Einzelinstanz mit SQLite. Der Scale-Modus
verwendet `compose.scale.yaml` und diese internen Variablen:

| Variable | Werte | Zweck |
| --- | --- | --- |
| `KEYWARS__RUNTIME__ROLE` | `all`, `web`, `arena`, `worker`, `migrate` | aktive Prozessrolle |
| `KEYWARS__DATABASE__PROVIDER` | `sqlite`, `postgresql` | Datenbankanbieter |
| `ConnectionStrings__KeyWars` | Verbindungszeichenfolge | PostgreSQL-Verbindung im Scale-Modus |
| `KEYWARS__REDIS__CONNECTION_STRING` | Verbindungszeichenfolge | SignalR, Data Protection und verteilter Zustand |
| `KEYWARS__CLUSTER__PROTOCOL_VERSION` | Releasewert, aktuell `3` | verhindert gemischte Redis-Laufzeitprotokolle |

Für Administratoren bleibt Compose die Referenz. Reihenfolge, Wartung und
Health-Prüfungen stehen unter [Skalierter Betrieb](scale-operations.md).

## Für jeden produktiven Start

| `.env`-Variable | Zweck |
| --- | --- |
| `KEYWARS_IMAGE`, `KEYWARS_VERSION` | Image und exakter Release-Tag; in Produktion nicht `latest` verwenden |
| `KEYWARS_BIND_ADDRESS` | standardmäßig `127.0.0.1` für einen Proxy auf demselben Host |
| `KEYWARS_PORT` | Host-Port, standardmäßig `8080` |
| `KEYWARS_LDAP_URLS` | Semikolonliste der Verzeichnisserver; standardmäßig `ldaps://` |
| `KEYWARS_LDAP_BASE_DN` | Suchwurzel des Verzeichnisses |
| `KEYWARS_LDAP_UPN_SUFFIX` | ergänzt kurze Anmeldenamen zu einem UPN |
| `KEYWARS_TIME_ZONE` | IANA-Zeitzone des Containers, standardmäßig `Europe/Berlin` |

LDAP-Details einschließlich `USER_BASE_DN`, StartTLS, Timeouts und eigener CA:
[LDAP und Active Directory](ldap.md).

## Content-Moderation

Moderationsrechte kommen ausschließlich aus den beim Login gelesenen direkten
LDAP-Gruppenwerten:

| `.env`-Variable | Inhalt |
| --- | --- |
| `KEYWARS_MODERATOR_GROUP_DNS` | Semikolonliste vollständiger `memberOf`-DNs; außerhalb von Development der einzige erlaubte Weg |
| `KEYWARS_MODERATOR_GROUP_VALUES` | Nur Development: Kurzwerte beziehungsweise erste RDN-Werte |

Leer bedeutet: keine Moderatoren. Vor einem produktiven Start Kurzwerte auf die
vollständigen DNs derselben Gruppen migrieren und `MODERATOR_GROUP_VALUES`
leeren. Nach einer Gruppenänderung muss sich die betroffene Person neu
anmelden; es gibt keine lokale Rollenzuweisung.

## Reverse Proxy

| `.env`-Variable | Standard | Bedeutung |
| --- | --- | --- |
| `KEYWARS_PROXY_KNOWN_PROXIES` | leer | Semikolonliste exakter Proxy-IP-Adressen |
| `KEYWARS_PROXY_KNOWN_NETWORKS` | leer | Semikolonliste enger Proxy-Netze in CIDR-Notation |

Ohne Eintrag vertraut ASP.NET Core nur Loopback-Proxys. Ungültige IP- oder
CIDR-Werte verhindern den Start. Es wird höchstens ein Proxy-Hop ausgewertet.
Siehe [Reverse Proxy](reverse-proxy.md).

## Live-Arena und Kapazität

Die ausgelieferten Werte sind für einen einzelnen, selbst gehosteten Container
konservativ. Erst nach Messung ändern:

| `.env`-Variable | Standard | Bedeutung |
| --- | ---: | --- |
| `KEYWARS_MAX_LIVE_PARTICIPANTS` | 64 | maximale Personen pro Raum |
| `KEYWARS_MAX_LIVE_ROOMS` | 200 | gleichzeitig im Speicher gehaltene Räume |
| `KEYWARS_MAX_ACTIVE_ROOMS_PER_CREATOR` | 5 | aktive Räume je erstellendem Profil; gilt auch für Revanchen |
| `KEYWARS_MAX_CONNECTIONS_PER_USER` | 3 | parallele Arena-Verbindungen pro Profil; wirksam 1 bis 20 |
| `KEYWARS_LIVE_BROADCAST_HZ` | 10 | maximale Progress-Broadcasts pro Sekunde und Raum |
| `KEYWARS_LIVE_COUNTDOWN_SECONDS` | 3 | Countdown; wirksam 1 bis 10 Sekunden |
| `KEYWARS_LIVE_RECONNECT_SECONDS` | 30 | Zeit für Wiederverbindungen; wirksam 0 bis 300 Sekunden |
| `KEYWARS_LIVE_COMMAND_QUEUE_CAPACITY` | 4096 | Pending-Kapazität koaleszierter Progress-Deltas |
| `KEYWARS_LIVE_COMPLETION_QUEUE_CAPACITY` | 4096 | Abschlussjobs; muss mindestens `KEYWARS_MAX_LIVE_ROOMS` entsprechen |
| `KEYWARS_LIVE_COMPLETION_DRAIN_TIMEOUT_SECONDS` | 10 | Drain vor Profilreset/-löschung; 1 bis 300 Sekunden |
| `KEYWARS_LIVE_COMPLETED_ROOM_RETENTION_MINUTES` | 60 | Aufbewahrung abgeschlossener Räume im Speicher |
| `KEYWARS_LIVE_LOBBY_ROOM_RETENTION_MINUTES` | 720 | Aufbewahrung inaktiver Lobbys im Speicher |
| `KEYWARS_MAX_ARENA_TARGET_GRAPHEMES` | 2800 | maximale Länge eines Arena-Zieltexts; wirksam 1 bis 2800 |
| `KEYWARS_LIVE_ROUND_DEADLINE_BASE_SECONDS` | 60 | Grundzeit jeder Runde; wirksam 15 bis 300 Sekunden |
| `KEYWARS_LIVE_ROUND_DEADLINE_MILLISECONDS_PER_GRAPHEME` | 1000 | zusätzliche Zeit je Zielgraphem; wirksam 100 bis 5000 Millisekunden |
| `KEYWARS_LIVE_ROUND_DEADLINE_MAX_SECONDS` | 3600 | harte Obergrenze einer Runde; wirksam 60 bis 3600 Sekunden |
| `KEYWARS_LIVE_ROUND_RESULTS_IDLE_SECONDS` | 120 | maximale Pause bis zur nächsten Serienrunde; wirksam 30 bis 600 Sekunden |

Ein gewerteter Zieleinlauf braucht mindestens zwei zeitlich getrennte,
monoton steigende Teilfortschritte, die der Server vor dem Ziel gesehen hat.
Ein vollständiger Direktsprung oder nur ein Vollabgleich bleibt sichtbar, aber
ungewertet.

Es gibt keine produktive Zuschauerrolle und deshalb kein Zuschauerlimit. Details:
[Live-Arena](live-arena.md).

## Weitere wirksame Anwendungsvariablen

Diese Werte werden bei Bedarf direkt unter `environment:` ergänzt:

| Variable | Standard | Grenze |
| --- | ---: | --- |
| `KEYWARS__AUTH__COOKIE_LIFETIME_HOURS` | 8 | 1 bis 12 Stunden |
| `KEYWARS__CONTENT__MAX_UPLOAD_BYTES` | 131072 | Importgröße |
| `KEYWARS__CONTENT__MAX_TEXT_CHARACTERS` | 20000 | UTF-16-Zeichen nach Normalisierung |
| `KEYWARS__CONTENT__MAX_TEXT_GRAPHEMES` | 20000 | Grapheme nach Normalisierung |
| `KEYWARS__CONTENT__MAX_TEXT_LINES` | 400 | Zeilen je importiertem Text |
| `KEYWARS_MAX_CHALLENGE_PARTICIPANTS` | 64 | mindestens 2; durch Compose übersetzt |
| `KEYWARS_SEASONS_ENABLED` | true | Saisonwertung und Rollover aktivieren |
| `KEYWARS_SEASONS_MONTHS_PER_SEASON` | 1 | 1, 2, 3, 4, 6 oder 12 Monate |
| `KEYWARS_SEASONS_ROLLOVER_CHECK_MINUTES` | 60 | Prüfung durch einen geleasten Worker; 5 bis 1440 Minuten |

`KEYWARS__AUTH__DEVELOPMENT_LOGIN=true` ist in Production gesperrt.
`KEYWARS__DATA__DIRECTORY` bleibt in der Einzelinstanz `/data`; dort liegen
SQLite, Data-Protection-Schlüssel und Backups. Im Scale-Modus liegen dauerhafte
Anwendungsdaten in PostgreSQL und der gemeinsame Schlüsselring in Redis. Es gibt
keinen automatischen Backup-Zeitplan – der Betreiber plant und exportiert
Backups selbst.

## Betriebsprüfungen

| Pfad | Aussage |
| --- | --- |
| `/health/live` | Prozess läuft |
| `/health/ready` | die für die Rolle benötigten Daten- und Laufzeitdienste sind erreichbar |
| `/health/arena-persistence` | Abschlussqueue, Fehler und Persistenzdauer |
| `/health/arena-progress` | aktive Räume, Deltas, Koaleszierungen, Drops und Broadcasts |
