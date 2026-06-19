# Datenmodell

Wichtige Tabellen:

- `UserProfiles`
- `TrainingTexts`
- `TextCollections`
- `TypingAttempts`
- `TypingAttemptErrors`
- `Challenges`
- `ChallengeParticipants`
- `ChallengeRounds`
- `ChallengeRoundResults`
- `ChallengeAttemptBindings`
- `LiveRoomSummaries`
- `Missions`
- `RewardLedgerEntries`
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

`ConsistencySampleCount`, `MeanWordMilliseconds` und `WordTimingVariation`
speichern minimale Wortzeit-Aggregate ohne vollstaendiges Keystroke-Replay.

## TypingAttemptErrors

`TypingAttemptErrors` speichert echte, per Alignment zugeordnete Fehler pro
Versuch: Position, Fehlerart, erwartetes Graphem, tatsaechliches Graphem und
betroffenes Muster. Die Tabelle ist Teil von Profil-Export und
Statistik-Reset/Loeschung.

## ChallengeAttemptBindings

Challenge-Versuche werden vor dem Tippen serverseitig gebunden. Ein Binding
enthaelt `ChallengeId`, `ChallengeRoundId`, `UserProfileId`, `TypingAttemptId`,
`TextSnapshotHash`, Modus und einen internen Bindungstoken. Unique-Indizes auf
`TypingAttemptId` und `(ChallengeRoundId, UserProfileId)` verhindern, dass freie
Training-Attempts oder wiederverwendete Attempts eine Challenge-Runde
abschliessen.

## Motivation

`Missions` besitzen einen stabilen `Key`, der die fachliche Mission definiert.
Anzeigetitel und Beschreibung koennen sich aendern, ohne Fortschritt oder
Auszahlung zu beeinflussen. Die Kombination aus `UserProfileId`, `MissionDate`
und `Key` ist eindeutig. Tagesmissionen verwenden das aktuelle lokale Datum,
Wochenmissionen den Montag der jeweiligen Woche als `MissionDate`.

`RewardLedgerEntries` modelliert XP-Buchungen idempotent. Jede Quelle bucht pro
Profil ueber `(UserProfileId, Source, SourceId)` genau einmal. Aktuell werden
Training-/Text-/Challenge-Versuche ueber die Attempt-ID, Arena-Ergebnisse ueber
den Raum-Idempotency-Key plus Profil-ID und Missionsbelohnungen ueber die
Mission-ID gebucht. Das Ledger ist Teil von Profil-Export und
Statistik-Reset/Loeschung.

## Profilaggregation

Die Profilseite nutzt `ProfileInsightsService` und laedt keine vollstaendige
Versuchsliste mehr in den Speicher. Trends, Gesamtwerte und Bestwerte werden
ueber SQL-Aggregate aus abgeschlossenen `TypingAttempts` berechnet. Die Historie
ist paginiert; Aktivitaet wird fuer die letzten 90 Tage aus Training, Arena und
erreichten Missionen zusammengesetzt.
