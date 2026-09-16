# Datenmodell

Wichtige Tabellen:

- `UserProfiles`
- `TrainingTexts`
- `TextCollections`
- `TextCollectionItems`
- `ContentModerationAuditEntries`
- `TypingAttempts`
- `TypingAttemptErrors`
- `Challenges`
- `ChallengeParticipants`
- `ChallengeRounds`
- `ChallengeRoundResults`
- `ChallengeAttemptBindings`
- `LiveRoomSummaries`
- `LiveRoomParticipantSummaries`
- `Missions`
- `RewardLedgerEntries`
- `Seasons`
- `SeasonScores`
- `Achievements`
- `GamificationEvents`
- `WeaknessObservations`

Gruppenwettbewerbe werden über Teilnehmerlisten modelliert, nicht über ein Opponent-Feld.

## UserProfiles

`UserProfiles` speichert neben den aus LDAP übernommenen Stammdaten auch
KeyWars-spezifische Vorlieben. Dazu gehören Sichtbarkeit in Ranglisten,
Live-WPM-/Ranghinweise, Sound-Opt-in, `SoundVolumePercent`, Reaktionen und
Reduced-Motion. Sounds bleiben standardmäßig deaktiviert; die Lautstärke ist
eine getrennte Profileinstellung.

## TypingAttempts

Ein Tippversuch durchläuft serverseitig eine klare Lebenszyklusphase:

- `Prepared`: Text, Nonce und Datenbankzeile sind vorbereitet, aber die autoritative Zeit läuft noch nicht.
- `Started`: ein gültiges Begin-Signal mit Nonce hat die Server-Startzeit gesetzt.
- `Finished`: Metriken und Motivation wurden erfolgreich persistiert.
- `Expired`: die Session wurde nach Ablauf der serverseitigen Lebensdauer bereinigt.
- `Aborted`: reserviert für explizite Abbruchpfade.

`PreparedAt` speichert den Zeitpunkt der Vorbereitung, `StartedAt` den autoritativen
Beginn für die Wertung. `ClientDurationMilliseconds` ist nur Diagnose; WPM und
Abschlussregeln verwenden die Serverdauer. `TextHash` bindet den verwendeten
Zieltext nachvollziehbar an den Versuch, ohne den kompletten Text im Versuch zu
duplizieren.

`ConsistencySampleCount`, `MeanWordMilliseconds` und `WordTimingVariation`
speichern minimale Wortzeit-Aggregate ohne vollständiges Keystroke-Replay.

## TypingAttemptErrors

`TypingAttemptErrors` speichert echte, per Alignment zugeordnete Fehler pro
Versuch: Position, Fehlerart, erwartetes Graphem, tatsächliches Graphem und
betroffenes Muster. Die Tabelle ist Teil von Profil-Export und
Statistik-Reset/Löschung.

## ChallengeAttemptBindings

Challenge-Versuche werden vor dem Tippen serverseitig gebunden. Ein Binding
enthält `ChallengeId`, `ChallengeRoundId`, `UserProfileId`, `TypingAttemptId`,
`TextSnapshotHash`, Modus und einen internen Bindungstoken. Unique-Indizes auf
`TypingAttemptId` und `(ChallengeRoundId, UserProfileId)` verhindern, dass freie
Training-Attempts oder wiederverwendete Attempts eine Challenge-Runde
abschließen.

## Arena- und Challenge-Rating

`LiveRoomParticipantSummaries` und `ChallengeParticipants` speichern
`RatingBefore`, `RatingDelta` und `RatingAfter`. Damit bleibt pro Ergebniszeile
nachvollziehbar, welcher Ratingstand in die Berechnung einging und welcher
Stand nach der transaktionalen Persistenz entstand. Serverabbrüche schreiben
eine neutrale Änderung mit identischem Before/After-Wert.

`LiveRoomSummaries.TargetTextHash` ist der SHA-256-Hash des normalisierten
Zieltexts, nicht der Klartext. Zusammen mit Modus und Serverabbruchstatus bildet
er das Readmodel für persönliche Arena-Bestwerte. Berücksichtigt werden nur
eigene beendete Teilnehmerzeilen aus persistierten, nicht serverseitig
abgebrochenen Räumen; alte Zeilen ohne Hash liefern keinen textbezogenen
Bestwert.

## Motivation

`Missions` besitzen einen stabilen `Key`, der die fachliche Mission definiert.
Anzeigetitel und Beschreibung können sich ändern, ohne Fortschritt oder
Auszahlung zu beeinflussen. Die Kombination aus `UserProfileId`, `MissionDate`
und `Key` ist eindeutig. Tagesmissionen verwenden das aktuelle lokale Datum,
Wochenmissionen den Montag der jeweiligen Woche als `MissionDate`.

`RewardLedgerEntries` modelliert XP-Buchungen idempotent. Jede Quelle bucht pro
Profil über `(UserProfileId, Source, SourceId)` genau einmal. Aktuell werden
Training-/Text-/Challenge-Versuche über die Attempt-ID, Arena-Ergebnisse über
den Raum-Idempotency-Key plus Profil-ID und Missionsbelohnungen über die
Mission-ID gebucht. Das Ledger ist Teil von Profil-Export und
Statistik-Reset/Löschung.

`Seasons` speichert feste UTC-Zeiträume. `SeasonScores` aggregiert die Punkte
pro Saison und Profil; die einzelne Punktevergabe steht mit Saison-ID und
Punktzahl auf demselben `RewardLedgerEntry` wie die idempotente Quelle. Frühere
Saisons bleiben erhalten. Profil-Reset und -Löschung entfernen die zugehörigen
Saisonwerte.

## Profilaggregation

Die Profilseite nutzt `ProfileInsightsService` und lädt keine vollständige
Versuchsliste mehr in den Speicher. Trends, Gesamtwerte und Bestwerte werden
über SQL-Aggregate aus abgeschlossenen `TypingAttempts` berechnet. Die Historie
ist paginiert. Gesamtwerte, Bestwerte, Historie und Aktivität folgen dem
gewählten Zeitraum von 7, 30 oder 90 Tagen; die Trendansicht vergleicht diese
drei Fenster mit dem jeweils vorherigen Zeitraum.
