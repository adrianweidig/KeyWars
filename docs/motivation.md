# Motivation

KeyWars nutzt Ziele, Missionen, XP, Level, Erfolge, Serien, Rivalen, Ranglisten und konkrete Empfehlungen. Es gibt keinen Shop, keine Währung, keine Lootboxen und keine künstliche Knappheit.

XP werden ueber ein Reward-Ledger gebucht. Pro Profil kann jede Quelle
(`attempt`, `arena`, `mission`) mit ihrer `SourceId` genau einmal XP vergeben.
Wiederholte Finish-Requests, doppelt persistierte Arena-Jobs oder bereits
ausgezahlte Missionen erzeugen dadurch keine zweite Auszahlung.

Die XP-Formel begrenzt Farmen: abgebrochene, inoffizielle, sehr kurze oder sehr
schnelle Ultrakurz-Versuche geben keine XP. Gueltige Versuche erhalten eine
gedeckelte WPM-Basis, Genauigkeitsboni, optional einen Bonus fuer persoenliche
Verbesserung und einen Bonus fuer anspruchsvollere gespeicherte Texte. Arena-
Ergebnisse nutzen denselben Buchungspfad mit eigener Quelle.

Arena- und Challenge-Rating nutzen eine paarweise Elo-Berechnung fuer 2 bis n
Teilnehmende. Die Platzierung entsteht aus Status, Dauer, Genauigkeit,
Fehlerzahl, Konsistenz und Roh-WPM; echte Gleichstaende erhalten denselben
Score. DNFs werden hinter beendeten Ergebnissen gewertet, Serverabbrueche
veraendern kein Rating. Pro Ergebniszeile werden `RatingBefore`, `RatingDelta`
und `RatingAfter` persistiert, damit die transaktionale Aenderung auditierbar
bleibt.

Level verwenden eine steigende Kurve. Level 2 beginnt bei 200 XP, Level 3 bei
450 XP, Level 4 bei 750 XP; danach waechst der Abstand pro Level weiter. Die
Startseite, das Profil und das Ergebnis nach einem Versuch zeigen den aktuellen
Fortschritt bis zum naechsten Level.

Missionen werden deterministisch pro Nutzer und Zeitraum erzeugt. Tagesmissionen
nutzen das lokale Datum der Instanz, Wochenmissionen den Montag der jeweiligen
Woche. Fortschritt haengt am stabilen Mission-Key, nicht am deutschen
Anzeigetitel.

Erfolge sind als stabile Definitionstabelle im Code hinterlegt. Die aktuelle
Definition umfasst Training, Praezision, Tempo, Serien, Arena, Texte, Team und
Missionen.

Der Coach ist deterministisch und lokal. Er betrachtet Genauigkeit, letzte
Versuche, Schwaechenbeobachtungen und aktuelle Missionen.
