# Architektur

KeyWars läuft als Single-Instance-Anwendung in genau einem Container. Kestrel, Razor Pages, Minimal APIs, SignalR, SQLite-Zugriff, Raumengine und Hintergrundlogik laufen im selben Prozess.

Persistente Daten liegen ausschließlich unter `/data`: SQLite-Datenbank, WAL/SHM-Dateien, Data-Protection-Schlüssel, Backups und Instanzkennung. Live-Räume liegen im Arbeitsspeicher; ein Neustart darf laufende Rennen abbrechen und erzeugt keine Ratingänderung.

Das Challenge-Modell verwendet `Challenge`, `ChallengeParticipant`, `ChallengeRound` und `ChallengeRoundResult`. Es gibt kein Creator/Opponent-Sonderfeld und keine Zwei-Personen-Annahme.

Die Live-Arena nutzt `LiveRoomManager` mit konfigurierbaren Kapazitätsgrenzen,
einer serverseitigen Raumphase und einem synchronisierten Countdown. Fortschritt
wird im Speicher verarbeitet; SQLite erhält nur zusammengefasste Ergebnisse.
Abschlussdaten laufen ueber `LiveRoomCompletionQueue` und
`SqliteLiveRoomCompletionWriter`: begrenzte In-Process-Queue, Idempotenz pro
Raum/Runde/Version, SQLite-Transaktion, Retry fuer transiente Locks und
Shutdown-Flush. Laufende Countdown- und Rennraeume werden beim Shutdown als
serverseitig abgebrochen gespeichert und bewirken keine Ratingaenderung.
Eine begrenzte Command-Queue mit Progress-Deltas ist als KW-015 geplant und
darf bis zur Implementierung nicht als produktiver Ist-Zustand beschrieben
werden.
