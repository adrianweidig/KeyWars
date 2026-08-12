# Scale-Modus betreiben

`compose.yaml` bleibt der einfache Standard mit einer KeyWars-Instanz und
SQLite. Der Scale-Modus trennt Web, Arena und Hintergrundarbeit und nutzt
PostgreSQL sowie Redis.

Alle Replikate eines Rollouts müssen dasselbe Cluster-Protokoll verwenden. Ein
normaler Start prüft `KEYWARS__CLUSTER__PROTOCOL_VERSION=1` gegen den stabilen
Redis-Marker `keywars:cluster:protocol-version` und bricht bei fehlendem oder
abweichendem Marker ab. Nur der explizite Cutover-Befehl setzt einen fehlenden
Marker; einen abweichenden vorhandenen Wert überschreibt er nie. Dafür zuerst
die Abschlussqueue des bisherigen Releases vollständig leerlaufen lassen und
erst danach **alle** KeyWars-Anwendungsreplikate stoppen. Der Cutover lehnt den
alten Redis-Namespace ab, solange `keywars:completion:pending` oder
`keywars:completion:failed` Einträge oder `keywars:completion:record:*`-Payloads
enthält; dabei bleibt der Protokollmarker unverändert. Abgelaufene
`keywars:completion:status:*`-Einträge blockieren nicht. Alte Daten nicht
löschen: Das bisherige Release erneut starten und drainen.
Erst dann Cutover, Datenbankmigration und neues Image starten. Bei späteren
Protokollwechseln zusätzlich die in den Release Notes genannten flüchtigen
Namespaces migrieren oder leeren. PostgreSQL-Daten und
`keywars:dataprotection:keys` bleiben unberührt.

| Betrieb | Geeignet für | Einstieg |
| --- | --- | --- |
| Einzelinstanz | ein Host, einfache Wartung | `compose.yaml` |
| Scale-Compose | Funktionsprüfung der verteilten Rollen | `compose.scale.yaml` |
| Swarm | mehrere Docker-Knoten, kleine Administration | `deploy/swarm/stack.yaml` |
| Kubernetes | Orchestrierung, HPA und NetworkPolicy | `deploy/k8s/` |

Web, Arena und Worker dürfen mehrere Replikate haben. Caddy leitet `/arena*`,
`/hubs/arena*`, `/api/arena*` und profil-löschende Vorgänge zum Arena-Service;
Affinitäts-Cookies sind nicht nötig. Redis hält Raumzustand und Zuordnung, eine
raumbezogene Lease und ein monotoner Compare-and-Swap-Zähler serialisieren
jede Änderung; veraltete Schreibversuche werden abgewiesen. Die mitgelieferten
Browser- und Lasttest-Clients verwenden dafür WebSockets ohne SignalR-
Negotiation, sodass Verbindungsaufbau und Socket nicht auf derselben Replik
landen müssen.
Beim Update verbinden sich Clients nach kurzer Unterbrechung neu.

Release v0.5 behandelt Redis als einen logischen Primary mit optionaler
Replikation. Ein Redis-Cluster verteilt den Raumzustand, aber noch nicht alle
Attempt-, Presence-, Progress-, Completion- und Profilzugriffs-Namespaces auf
mehrere Slots. Diese Grenze gehört vor großen Lastabnahmen in die
Kapazitätsplanung.

Der mitgelieferte Arena-HPA nutzt CPU als überall verfügbaren Startwert und
skaliert langsam zurück. Mit Prometheus Adapter können zusätzlich
`keywars_rooms_active`, `keywars_participants_active` und Command-/Progress-
Latenzen einfließen. Queue-Tiefe und ältester Persistenzauftrag sind primär
Schutz- und Alarmsignale, nicht alleinige Scale-Metriken.

Automatische Datenaufbewahrung startet sicherheitshalber deaktiviert und im
Dry-Run. Ein erneuerbarer Redis-Maintenance-Lease lässt auch bei mehreren
Worker-Repliken nur einen Lauf zu. Backup-Retention bleibt eine Funktion der
SQLite-Einzelinstanz.

Keine feste Replikazahl garantiert eine Nutzerzahl. Vor einer Freigabe Last,
LDAP, Datenbank, Redis, Netzwerk und Ausfallverhalten mit der eigenen
Raumgröße messen; siehe [Lasttests](load-testing.md).

## Gemeinsamer Vertrag

- Rollen: `all`, `web`, `arena`, `worker`, `migrate` über
  `KEYWARS__RUNTIME__ROLE`.
- Scale-Datenbank: `KEYWARS__DATABASE__PROVIDER=postgresql` und
  `ConnectionStrings__KeyWars`.
- Redis: `KEYWARS__REDIS__CONNECTION_STRING`.
- Nur `migrate` ändert das PostgreSQL-Schema und beendet sich danach.
- `maintenance cluster-protocol cutover --confirm-apps-stopped` setzt den
  Redis-Protokollmarker idempotent; die Bestätigung ist bewusst verpflichtend.
- `/health/live` prüft den Prozess, `/health/ready` zusätzlich PostgreSQL und
  Redis.
- Der öffentliche Weg muss TLS terminieren. Port `8080` ist nur das Backend
  eines HTTPS-Proxys; siehe [Reverse Proxy](reverse-proxy.md).

## Images spiegeln oder vorab laden

Caddy, PostgreSQL und Redis sind in den Manifesten auf konkrete Version und
Registry-Digest gepinnt. Vor einem Offline-Rollout diese Fremdimages sowie das
KeyWars-Release-Image auf jedem Ziel laden oder unverändert in eine interne
Registry kopieren. `deploy/images.txt` ist die maschinenlesbare Ausgangsliste.
Swarm kann abweichende, ebenfalls digest-gepinnte
Mirror-Referenzen über `CADDY_IMAGE`, `POSTGRES_IMAGE` und `REDIS_IMAGE`
erhalten. In Kubernetes die `images`-Einträge per Site-Overlay auf den internen
Namen und dessen Digest setzen. Ein Tag allein ist kein Air-Gap-Nachweis.

Nach Veröffentlichung von v0.5.0 den GHCR-Digest des KeyWars-Images erfassen
und im produktiven Overlay beziehungsweise in der Swarm-Umgebung festhalten.
Das Repository kann diesen Digest nicht vor dem tatsächlichen Image-Publish
vorwegnehmen.

## Compose: kleinster Scale-Aufbau

```powershell
Copy-Item .env.scale.example .env.scale
$secretDirectory = Join-Path $PWD "secrets"
New-Item -ItemType Directory -Force $secretDirectory | Out-Null
$securePassword = Read-Host "Neues PostgreSQL-Kennwort" -AsSecureString
$credential = [pscredential]::new("keywars", $securePassword)
$plainPassword = $credential.GetNetworkCredential().Password
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $secretDirectory "postgres-password"), $plainPassword, $utf8)
$quotedPassword = $plainPassword.Replace('"', '""')
$connection = "Host=postgres;Port=5432;Database=keywars;Username=keywars;Password=`"$quotedPassword`""
[IO.File]::WriteAllText((Join-Path $secretDirectory "database-connection"), $connection, $utf8)
[IO.File]::WriteAllText((Join-Path $secretDirectory "redis-connection"), "redis:6379,abortConnect=false", $utf8)
Remove-Variable plainPassword, quotedPassword, connection, credential, securePassword
# Die interne LDAP-CA als secrets/ldap-ca.crt ablegen.
docker compose --env-file .env.scale -f compose.scale.yaml config
$queue = Invoke-RestMethod "http://127.0.0.1:8080/health/arena-persistence"
if ($queue.pendingJobs -ne 0 -or $queue.failedRecords -ne 0) { throw "Arena-Abschlussqueue zuerst leerlaufen lassen." }
docker compose --env-file .env.scale -f compose.scale.yaml stop keywars-edge keywars-web keywars-arena keywars-worker
docker compose --env-file .env.scale -f compose.scale.yaml up -d postgres redis
docker compose --env-file .env.scale -f compose.scale.yaml run --rm keywars-protocol-cutover
docker compose --env-file .env.scale -f compose.scale.yaml run --rm keywars-migrate
docker compose --env-file .env.scale -f compose.scale.yaml up -d keywars-edge keywars-web keywars-arena keywars-worker
```

`.env.scale` bleibt außerhalb von Git und bekommt nur für den Administrator
Leserechte. Der sichere Standard bindet Caddy nur an `127.0.0.1`; ein lokaler
TLS-Proxy spricht diesen Port an. Vor einem Upgrade: [Backup](backup-restore.md),
`pendingJobs=0` und `failedRecords=0` abwarten, die vier lang laufenden
Anwendungsdienste stoppen, Images laden, den Cutover und `keywars-migrate`
erfolgreich ausführen und Dienste wieder starten. Die beiden
Einmal-Dienste liegen im Profil `operations` und starten nie versehentlich mit
`docker compose up`. Compose ist weder Datenbank-HA noch ein Ersatz für
Swarm/Kubernetes.

## Swarm

Voraussetzungen: aktuelles Docker Engine mit `replicated-job`, ein externer
TLS-Proxy und ein gesicherter Ablageort für die Site-Konfiguration.

```bash
data_node="$(docker info --format '{{.Name}}')"
docker node update --label-add keywars.data=true "$data_node"
chmod 600 /etc/keywars/keywars-scale.env
set -a; . /etc/keywars/keywars-scale.env; set +a
docker config create keywars-ldap-ca "$KEYWARS_LDAP_CA_FILE"
sh deploy/swarm/create-secrets.sh
```

Die drei Secrets heißen `keywars-postgres-password`,
`keywars-database-connection` und `keywars-redis-connection`. Das Skript
überschreibt vorhandene Secrets nicht. Für externes Redis kann
`KEYWARS_REDIS_CONNECTION` nur für diesen Skriptaufruf gesetzt werden.
Eine neue LDAP-CA als neue Docker Config anlegen und deren Namen über
`KEYWARS_LDAP_CA_CONFIG` umstellen; die alte Config erst danach entfernen.
Secrets ebenso unter einem neuen Namen anlegen und über
`KEYWARS_POSTGRES_PASSWORD_SECRET`, `KEYWARS_DATABASE_CONNECTION_SECRET` und
`KEYWARS_REDIS_CONNECTION_SECRET` atomar umstellen. Ein PostgreSQL-Kennwort
dabei kontrolliert in Datenbank, Server-Secret und Connection-Secret
konsistent ändern.

Bei Upgrades zuerst über `/health/arena-persistence` `pendingJobs=0` und
`failedRecords=0` abwarten. Danach Edge, Web, Arena und Worker auf null
skalieren und den Stillstand prüfen. Dann Infrastruktur, Protokoll-Cutover,
Migration und zuletzt die Anwendung starten:

```bash
KEYWARS_EDGE_REPLICAS=0 KEYWARS_WEB_REPLICAS=0 KEYWARS_ARENA_REPLICAS=0 \
KEYWARS_WORKER_REPLICAS=0 KEYWARS_CUTOVER_REPLICAS=0 KEYWARS_MIGRATE_REPLICAS=0 \
docker stack deploy -c deploy/swarm/stack.yaml keywars

docker stack services keywars
docker service ps keywars_keywars-postgres
docker service ps keywars_keywars-redis

KEYWARS_EDGE_REPLICAS=0 KEYWARS_WEB_REPLICAS=0 KEYWARS_ARENA_REPLICAS=0 \
KEYWARS_WORKER_REPLICAS=0 KEYWARS_CUTOVER_REPLICAS=1 KEYWARS_MIGRATE_REPLICAS=0 \
docker stack deploy -c deploy/swarm/stack.yaml keywars
docker service logs keywars_keywars-protocol-cutover
docker service ps keywars_keywars-protocol-cutover --no-trunc

KEYWARS_EDGE_REPLICAS=0 KEYWARS_WEB_REPLICAS=0 KEYWARS_ARENA_REPLICAS=0 \
KEYWARS_WORKER_REPLICAS=0 KEYWARS_CUTOVER_REPLICAS=0 KEYWARS_MIGRATE_REPLICAS=1 \
docker stack deploy -c deploy/swarm/stack.yaml keywars
docker service logs keywars_keywars-migrate
docker service ps keywars_keywars-migrate --no-trunc

docker stack deploy -c deploy/swarm/stack.yaml keywars
```

Nur nach Status `Complete` beider Jobs den letzten Befehl ausführen. Web, Arena
und Worker nutzen bewusst `stop-first` ohne automatischen Rollback. Bei jedem
Upgrade dieselbe Reihenfolge mit neuem `KEYWARS_VERSION` verwenden.
Die Standardnetze `10.42.10.0/24` und `10.42.11.0/24` bei Konflikten über
`KEYWARS_FRONTEND_SUBNET` und `KEYWARS_BACKEND_SUBNET` ändern; das Frontendnetz
dann auch als `KEYWARS_PROXY_KNOWN_NETWORKS` setzen.

Nützliche Befehle:

```bash
docker stack services keywars
docker service ps keywars_keywars-web --no-trunc
docker service logs --since 15m keywars_keywars-arena
docker service scale keywars_keywars-web=4 keywars_keywars-worker=2
docker service rollback keywars_keywars-web
```

`rollback` ist nur bei gleichem Cluster-Protokoll und rückwärtskompatiblem
Schema sicher. Sonst Image, PostgreSQL-Backup und den in den Release Notes
beschriebenen Redis-Protokollzustand gemeinsam wiederherstellen. Das
Routing-Mesh veröffentlicht `8080` auf Swarm-Knoten; Firewallzugriff auf den
TLS-Proxy begrenzen.

## Kubernetes

Die Basis erwartet extern betriebenes PostgreSQL und Redis. Zusätzlich werden
Metrics Server für die HPAs und ein CNI benötigt, das NetworkPolicy tatsächlich
durchsetzt. Die mitgelieferte Policy erlaubt ausgehend DNS, PostgreSQL, Redis,
LDAP und optional OTLP; Ziel-CIDRs installationsspezifisch enger setzen. Zum
direkten Prometheus-Scrape den Monitoring-Namespace mit
`keywars.io/metrics-access=true` kennzeichnen. Caddy veröffentlicht `/metrics`
nicht nach außen.

Namespace und nicht geheime Laufzeitwerte anlegen:

```bash
kubectl apply -f deploy/k8s/namespace.yaml -f deploy/k8s/runtime-config.yaml
kubectl -n keywars create configmap keywars-site-config \
  --from-env-file=keywars-site.env --dry-run=client -o yaml | kubectl apply -f -
```

`keywars-site.env` liegt nicht im Repository. Es enthält mindestens LDAP-URLs,
Base-DN, UPN-Suffix und das exakte Pod-Netz als
`KEYWARS__PROXY__KNOWN_NETWORKS`. Bei privater CA zusätzlich
`KEYWARS__LDAP__CA_CERTIFICATE_PATH=/etc/keywars/ldap-ca/ca.crt` setzen und die
Config anlegen:

```bash
kubectl -n keywars create configmap keywars-ldap-ca \
  --from-file=ca.crt="$KEYWARS_LDAP_CA_FILE" --dry-run=client -o yaml | kubectl apply -f -
```

Moderation wird optional mit semikolongetrennten
`KEYWARS__MODERATION__MODERATOR_GROUP_DNS` und
`KEYWARS__MODERATION__MODERATOR_GROUP_VALUES` freigeschaltet. Beide leeren
Werte lassen sie fail-closed deaktiviert.

Secrets ohne Klartextdatei im Repository anwenden:

```bash
printf '%s' "$KEYWARS_DB_CONNECTION" | kubectl -n keywars create secret generic keywars-database-connection \
  --from-file=connection-string=/dev/stdin --dry-run=client -o yaml | kubectl apply -f -
printf '%s' "$KEYWARS_REDIS_CONNECTION" | kubectl -n keywars create secret generic keywars-redis-connection \
  --from-file=connection-string=/dev/stdin --dry-run=client -o yaml | kubectl apply -f -
```

Bei einem Upgrade zuerst über `/health/arena-persistence` `pendingJobs=0` und
`failedRecords=0` abwarten. Danach alle laufenden KeyWars-Anwendungs-Pods
stoppen und das Ergebnis prüfen. Der Cutover ist ein eigener Job; Migration und
Rollout bleiben getrennt:

```bash
kubectl -n keywars delete hpa keywars-web keywars-arena keywars-worker --ignore-not-found
kubectl -n keywars scale deployment/keywars-edge deployment/keywars-web \
  deployment/keywars-arena deployment/keywars-worker --replicas=0
kubectl -n keywars wait --for=delete pod \
  -l 'app.kubernetes.io/component in (edge,web,arena,worker)' --timeout=5m
kubectl -n keywars delete job keywars-protocol-cutover --ignore-not-found
kubectl apply -f deploy/k8s/runtime-config.yaml
kubectl apply -k deploy/k8s/cutover
kubectl -n keywars wait --for=condition=complete job/keywars-protocol-cutover --timeout=5m
kubectl -n keywars logs job/keywars-protocol-cutover
kubectl -n keywars delete job keywars-migrate --ignore-not-found
kubectl apply -k deploy/k8s/migration
kubectl -n keywars wait --for=condition=complete job/keywars-migrate --timeout=15m
kubectl -n keywars logs job/keywars-migrate
kubectl apply -k deploy/k8s
kubectl -n keywars rollout status deployment/keywars-web --timeout=5m
kubectl -n keywars rollout status deployment/keywars-arena --timeout=5m
```

Bei der Erstinstallation entfällt nur der `scale`-/`wait`-Block, weil noch
keine Anwendungs-Pods existieren. Die Deployments verwenden `Recreate`; dadurch
erzwingt Kubernetes keinen stillen Mischbetrieb oder automatischen Rollback.

`keywars-edge` ist absichtlich `ClusterIP`. Ein vorhandener TLS-Ingress zeigt
auf Service `keywars-edge`, Port `8080`; Hostname, Zertifikat und
Ingress-spezifische Annotationen bleiben Site-Konfiguration.

```bash
kubectl -n keywars get deployments,pods,hpa,pdb
kubectl -n keywars get events --sort-by=.lastTimestamp
kubectl -n keywars logs deployment/keywars-arena --since=15m
kubectl -n keywars rollout undo deployment/keywars-web
```

Auch `rollout undo` ist nur innerhalb derselben Cluster-Protokollversion sicher.
Bei einem Protokollwechsel gilt die gemeinsame Wiederherstellung von Image,
PostgreSQL und dem dokumentierten Redis-Protokollzustand.

Kubernetes setzt CPU-/Speichergrenzen, aber keine portable Pod-Option für
`nofile`. Den Wert auf den Knoten beziehungsweise in der Container-Runtime
prüfen. Änderungen werden über `preStop`, Readiness und 60 Sekunden
Terminierungsfrist abgewickelt; einen separaten Drain-Endpunkt gibt es nicht.

## Abnahme und Alarmierung

Nach Installation und Upgrade mindestens prüfen:

1. Öffentlicher HTTPS-Aufruf von `/health/ready`.
2. LDAP-Anmeldung und Abmeldung.
3. Zwei Browser in einer Live-Arena, inklusive Wiederverbindung.
4. Web-Replik erhöhen, erneut anmelden und normalen Tippversuch abschließen.
5. Eine Arena-Replik neu starten und Wiederverbindung sowie Raumübernahme
   prüfen.
6. Backup exportieren und Restore regelmäßig isoliert testen.

Alarmieren auf nicht bereite Replikate, Neustartschleifen, PostgreSQL- und
Redis-Fehler, wachsende Arena-Queues, hohe Latenz und knappen Speicher. Secrets
versioniert rotieren; alte Secrets erst entfernen, wenn kein Dienst sie mehr
referenziert.
