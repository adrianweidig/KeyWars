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

`compose.scale.yaml` trennt Laufzeitrollen:

| Rolle | Verantwortung |
| --- | --- |
| `web` | Razor Pages, HTTP-Endpunkte und Anmeldung |
| `arena` | SignalR und Live-Arena |
| `worker` | asynchrone Abschluss- und Hintergrundarbeit |
| `migrate` | einmalige PostgreSQL-Migration vor dem Start |
| `all` | kombinierte Rolle der Einzelinstanz |

PostgreSQL speichert dauerhafte Daten. Redis stellt Data-Protection-Schlüssel,
SignalR-Backplane und verteilten Laufzeitzustand bereit. `web`, `arena` und
`worker` starten im Scale-Modus ohne diese Abhängigkeiten nicht. Swarm und
Kubernetes verwenden dieselben Rollen; die Referenz für Betrieb und Wartung ist
weiterhin Compose. Details: [Skalierter Betrieb](scale-operations.md).

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
