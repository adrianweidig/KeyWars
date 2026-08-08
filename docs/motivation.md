# Motivation

KeyWars nutzt Ziele, Missionen, XP, Level, Erfolge, Serien, Rivalen,
Ranglisten und konkrete Empfehlungen. Es gibt keinen Shop, keine Währung,
keine Lootboxen und keine künstliche Knappheit.

XP werden über ein Reward-Ledger gebucht. Pro Profil kann jede Quelle
(`attempt`, `arena`, `mission`) mit ihrer `SourceId` genau einmal XP vergeben.
Wiederholte Finish-Requests, doppelt persistierte Arena-Jobs oder bereits
ausgezahlte Missionen erzeugen dadurch keine zweite Auszahlung.

Die XP-Formel begrenzt Farmen: abgebrochene, inoffizielle, sehr kurze oder sehr
schnelle Ultrakurz-Versuche geben keine XP. Gültige Versuche erhalten eine
gedeckelte WPM-Basis, Genauigkeitsboni, optional einen Bonus für persönliche
Verbesserung und einen Bonus für anspruchsvollere gespeicherte Texte. Arena-
Ergebnisse nutzen denselben Buchungspfad mit eigener Quelle.

Arena- und Challenge-Rating nutzen eine paarweise Elo-Berechnung für 2 bis n
Teilnehmende. Die Platzierung entsteht aus Status, Dauer, Genauigkeit,
Fehlerzahl, Konsistenz und Roh-WPM; echte Gleichstände erhalten denselben
Score. DNFs werden hinter beendeten Ergebnissen gewertet, Serverabbrüche
verändern kein Rating. Pro Ergebniszeile werden `RatingBefore`, `RatingDelta`
und `RatingAfter` persistiert, damit die transaktionale Änderung auditierbar
bleibt.

Level verwenden eine steigende Kurve. Level 2 beginnt bei 200 XP, Level 3 bei
450 XP, Level 4 bei 750 XP; danach wächst der Abstand pro Level weiter. Die
Startseite, das Profil und das Ergebnis nach einem Versuch zeigen den aktuellen
Fortschritt bis zum nächsten Level.

Missionen werden deterministisch pro Nutzer und Zeitraum erzeugt. Tagesmissionen
nutzen das lokale Datum der Instanz, Wochenmissionen den Montag der jeweiligen
Woche. Fortschritt hängt am stabilen Mission-Key, nicht am deutschen
Anzeigetitel.

Erfolge sind als stabile Definitionstabelle im Code hinterlegt. Die aktuelle
Definition umfasst Training, Präzision, Tempo, Serien, Arena, Texte, Team und
Missionen.

Der Coach ist deterministisch und lokal. Er betrachtet Genauigkeit, letzte
Versuche, Schwächenbeobachtungen und aktuelle Missionen.
