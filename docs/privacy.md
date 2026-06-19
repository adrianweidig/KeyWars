# Datenschutz und lokale Profildaten

KeyWars speichert lokale Profildaten nur in der eigenen SQLite-Datenbank. Die
AD- oder LDAP-Quelle bleibt führend für Identität und Login.

## Export

Der Profilexport enthält nur Daten des angemeldeten Profils:

- Profilstammdaten und KeyWars-Einstellungen;
- Tippversuche;
- Reward-Ledger, Missionen und Erfolge;
- Schwächenbeobachtungen;
- eigene Texte und Sammlungen;
- eigene Challenge-Teilnahmen und Rundenergebnisse;
- eigene Live-Arena-Ergebniszeilen.

Der Export enthält eine Versionsnummer und einen Erstellzeitpunkt.

## Statistik Zurücksetzen

Der Reset muss im Formular mit dem aktuellen AD-/LDAP-Kontonamen bestätigt
werden. Eine falsche Eingabe bricht die Aktion serverseitig ab und lässt die
Statistiken unverändert.

Der Statistik-Reset ist transaktional. Er entfernt Tippversuche, Reward-Ledger,
Missionen, Erfolge und Schwächenbeobachtungen. XP, Level, Serie, Saisonpunkte,
Arena-Rating und gewertete Matchanzahl werden auf Startwerte gesetzt.

AD-Identität, Profilangaben, eigene Texte und Sammlungen bleiben erhalten.

## Profil Löschen

Die Löschung muss im Formular mit dem aktuellen AD-/LDAP-Kontonamen bestätigt
werden. Eine falsche Eingabe bricht die Aktion serverseitig ab; das Profil
bleibt aktiv und die Sitzung bleibt bestehen.

Die Profil-Löschung pseudonymisiert das lokale Profil und meldet die Sitzung
ab. Directory-Identifier, Namen, E-Mail, Abteilung, Titel und Motto werden
entfernt oder durch einen gelöschten Profilbezeichner ersetzt. Ranglisten-,
Ghost- und Challenge-Freigaben werden deaktiviert.

Private Texte werden geleert und eigene Sammlungen entfernt. Aktive
Challenge-Teilnahmen werden abgelehnt. Aktive Live-Arena-Teilnahmen werden aus
dem laufenden In-Memory-Raum entfernt: vor dem Start als verlassen, während
eines Rennens als nicht beendet.

Historische Gruppen- und Arena-Ergebnisse bleiben zur Integrität der
gemeinsamen Ergebnislisten erhalten, zeigen aber nur noch das gelöschte Profil.
Ein späterer AD-Login mit derselben Directory-Identität erzeugt ein neues
KeyWars-Profil und wird nicht mit dem gelöschten Profil verknüpft.
