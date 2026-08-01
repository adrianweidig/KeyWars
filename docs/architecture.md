# Architektur

KeyWars läuft als Single-Instance-Anwendung in genau einem Container. Kestrel, Razor Pages, Minimal APIs, SignalR, SQLite-Zugriff, Raumengine und Hintergrundlogik laufen im selben Prozess.

Persistente Daten liegen ausschließlich unter `/data`: SQLite-Datenbank, WAL/SHM-Dateien, Data-Protection-Schlüssel, Backups und Instanzkennung. Live-Räume liegen im Arbeitsspeicher; ein Neustart darf laufende Rennen abbrechen und erzeugt keine Ratingänderung.

Das Challenge-Modell verwendet `Challenge`, `ChallengeParticipant`, `ChallengeRound` und `ChallengeRoundResult`. Es gibt kein Creator/Opponent-Sonderfeld und keine Zwei-Personen-Annahme.

Die Live-Arena nutzt `LiveRoomManager` mit konfigurierbaren Kapazitätsgrenzen,
einer serverseitigen Raumphase und einem synchronisierten Countdown. Fortschritt
wird im Speicher verarbeitet; SQLite erhält nur zusammengefasste Ergebnisse.
Öffentliche Verträge liegen in `LiveRoomContracts`, der veränderliche interne
Zustand in `LiveRoomState`, Fortschrittsberechnungen in `LiveRoomProgress` und
Runden-, Serien- sowie Teamwertungen in `LiveRoomScoring`. Der Manager
koordiniert diese Bausteine unter den jeweiligen Raumsperren.
Abschlussdaten laufen über `LiveRoomCompletionQueue` und
`SqliteLiveRoomCompletionWriter`: begrenzte In-Process-Queue, Idempotenz pro
Raum/Runde/Version, SQLite-Transaktion, Retry für transiente Locks und
Shutdown-Flush. Laufende Countdown- und Rennräume werden beim Shutdown als
serverseitig abgebrochen gespeichert und bewirken keine Ratingänderung.
Der heiße SignalR-Progresspfad sendet keine Vollsnapshots mehr, sondern
koaleszierte `LiveProgressDelta`-Batches über `LiveProgressBroadcaster`.
Zuverlässige Raumereignisse bleiben direkte Commands mit Vollsnapshot;
eine vollständige RoomCommand-Pipeline für alle Befehle bleibt weiterer
KW-015-/KW-052-Ausbau.
