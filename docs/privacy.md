# Datenschutz und lokale Profildaten

KeyWars speichert lokale Profildaten nur in der eigenen SQLite-Datenbank. Die
AD- oder LDAP-Quelle bleibt fuehrend fuer Identitaet und Login.

## Export

Der Profilexport enthaelt nur Daten des angemeldeten Profils:

- Profilstammdaten und KeyWars-Einstellungen;
- Tippversuche;
- Reward-Ledger, Missionen und Erfolge;
- Schwaechenbeobachtungen;
- eigene Texte und Sammlungen;
- eigene Challenge-Teilnahmen und Rundenergebnisse;
- eigene Live-Arena-Ergebniszeilen.

Der Export enthaelt eine Versionsnummer und einen Erstellzeitpunkt.

## Statistik Zuruecksetzen

Der Statistik-Reset ist transaktional. Er entfernt Tippversuche, Reward-Ledger,
Missionen, Erfolge und Schwaechenbeobachtungen. XP, Level, Serie, Saisonpunkte,
Arena-Rating und gewertete Matchanzahl werden auf Startwerte gesetzt.

AD-Identitaet, Profilangaben, eigene Texte und Sammlungen bleiben erhalten.

## Profil Loeschen

Die Profil-Loeschung pseudonymisiert das lokale Profil und meldet die Sitzung
ab. Directory-Identifier, Namen, E-Mail, Abteilung, Titel und Motto werden
entfernt oder durch einen gelöschten Profilbezeichner ersetzt. Ranglisten-,
Ghost- und Challenge-Freigaben werden deaktiviert.

Private Texte werden geleert und eigene Sammlungen entfernt. Aktive
Challenge-Teilnahmen werden abgelehnt. Aktive Live-Arena-Teilnahmen werden aus
dem laufenden In-Memory-Raum entfernt: vor dem Start als verlassen, waehrend
eines Rennens als nicht beendet.

Historische Gruppen- und Arena-Ergebnisse bleiben zur Integritaet der
gemeinsamen Ergebnislisten erhalten, zeigen aber nur noch das geloeschte Profil.
Ein spaeterer AD-Login mit derselben Directory-Identitaet erzeugt ein neues
KeyWars-Profil und wird nicht mit dem geloeschten Profil verknuepft.
