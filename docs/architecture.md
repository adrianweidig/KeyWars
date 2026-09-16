# Architektur

KeyWars hat zwei Betriebsarten mit demselben Anwendungscode.

## Einzelinstanz

`compose.yaml` startet einen ASP.NET-Core-Prozess mit Razor Pages, Minimal APIs,
SignalR, Raumengine und Hintergrundarbeit. SQLite, Data-Protection-Schlüssel und
Backups liegen unter `/data`. Live-Räume und vorbereitete Tippversuche sind
prozesslokal; ein Neustart darf laufende Rennen ohne Ratingänderung abbrechen.

Dieser Modus ist der einfache Standard für einen einzelnen Host. Er wird nicht
durch zusätzliche Replikate desselben Compose-Dienstes skaliert.

## Scale-Modus

`compose.scale.yaml` trennt Laufzeit- und Routingrollen:

| Rolle | Verantwortung |
| --- | --- |
| `web` | öffentliches Standardziel für Razor Pages, HTTP-Endpunkte und Anmeldung |
| `arena` | öffentliches Ziel für Arena-, Hub- und profil-löschende Routen; hostet dafür ebenfalls Razor Pages und APIs |
| `worker` | asynchrone Abschluss- und Hintergrundarbeit |
| `migrate` | einmalige PostgreSQL-Migration vor dem Start |
| `all` | kombinierte Rolle der Einzelinstanz |

PostgreSQL speichert dauerhafte Daten. Redis stellt Data-Protection-Schlüssel,
SignalR-Backplane und verteilten Laufzeitzustand bereit. `web`, `arena` und
`worker` starten im Scale-Modus ohne diese Abhängigkeiten nicht. Swarm und
Kubernetes verwenden dieselben Rollen; die Referenz für Betrieb und Wartung ist
weiterhin Compose. Details: [Skalierter Betrieb](scale-operations.md).

Die Trennung von `web` und `arena` ist im aktuellen Code eine Verantwortung des
Edge-Routings, keine harte Endpoint-Isolation im Prozess. Beide Rollen hosten
die Anwendung; nur `arena` mappt zusätzlich den SignalR-Hub. Caddy leitet die
Arena- und Datenschutzpfade gezielt dorthin.

Im Scale-Modus liegt jeder Raumzustand unter einem eigenen Redis-Hash-Tag.
Attempt-, Presence-, Progress-, Completion- und Profilzugriffszustand wird unter
Protokoll v2 auf 256 Buckets verteilt; Verzeichnisse und Admission-Zähler bilden
eine globale Kontrollebene. Der Protokoll-Cutover läuft bei gestoppten
Anwendungsrollen vor der Datenbankmigration.

## Fachliche Grenzen

Das Challenge-Modell verwendet `Challenge`, `ChallengeParticipant`,
`ChallengeRound` und `ChallengeRoundResult`; es gibt keine Zwei-Personen-Annahme.

Die Live-Arena verarbeitet Tippfortschritt transient und persistiert nur
zusammengefasste Ergebnisse. `LiveRoomContracts` enthält öffentliche Verträge,
`LiveRoomState` den veränderlichen Zustand, `LiveRoomProgress` die
Fortschrittsberechnung und `LiveRoomScoring` die Wertung.

Abschlussdaten laufen idempotent über `LiveRoomCompletionQueue`. Der heiße
SignalR-Pfad sendet koaleszierte `LiveProgressDelta`-Batches; zuverlässige
Raumereignisse bleiben vollständige Commands.
