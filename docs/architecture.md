# Architektur

KeyWars läuft als Single-Instance-Anwendung in genau einem Container. Kestrel, Razor Pages, Minimal APIs, SignalR, SQLite-Zugriff, Raumengine und Hintergrundlogik laufen im selben Prozess.

Persistente Daten liegen ausschließlich unter `/data`: SQLite-Datenbank, WAL/SHM-Dateien, Data-Protection-Schlüssel, Backups und Instanzkennung. Live-Räume liegen im Arbeitsspeicher; ein Neustart darf laufende Rennen abbrechen und erzeugt keine Ratingänderung.

Das Challenge-Modell verwendet `Challenge`, `ChallengeParticipant`, `ChallengeRound` und `ChallengeRoundResult`. Es gibt kein Creator/Opponent-Sonderfeld und keine Zwei-Personen-Annahme.

Die Live-Arena nutzt `LiveRoomManager` mit konfigurierbaren Kapazitätsgrenzen und begrenzten Channels für Fortschrittskommandos. Fortschritt wird im Speicher verarbeitet; SQLite erhält nur zusammengefasste Ergebnisse.
