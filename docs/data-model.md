# Datenmodell

Wichtige Tabellen:

- `UserProfiles`
- `TrainingTexts`
- `TextCollections`
- `TypingAttempts`
- `Challenges`
- `ChallengeParticipants`
- `ChallengeRounds`
- `ChallengeRoundResults`
- `LiveRoomSummaries`
- `Missions`
- `Achievements`
- `WeaknessObservations`

Gruppenwettbewerbe werden über Teilnehmerlisten modelliert, nicht über ein Opponent-Feld.

## TypingAttempts

Ein Tippversuch durchlaeuft serverseitig eine klare Lebenszyklusphase:

- `Prepared`: Text, Nonce und Datenbankzeile sind vorbereitet, aber die autoritative Zeit laeuft noch nicht.
- `Started`: ein gueltiges Begin-Signal mit Nonce hat die Server-Startzeit gesetzt.
- `Finished`: Metriken und Motivation wurden erfolgreich persistiert.
- `Expired`: die Session wurde nach Ablauf der serverseitigen Lebensdauer bereinigt.
- `Aborted`: reserviert fuer explizite Abbruchpfade.

`PreparedAt` speichert den Zeitpunkt der Vorbereitung, `StartedAt` den autoritativen
Beginn fuer die Wertung. `ClientDurationMilliseconds` ist nur Diagnose; WPM und
Abschlussregeln verwenden die Serverdauer. `TextHash` bindet den verwendeten
Zieltext nachvollziehbar an den Versuch, ohne den kompletten Text im Versuch zu
duplizieren.
