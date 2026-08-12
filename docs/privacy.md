# Datenschutz und lokale Profildaten

KeyWars speichert Profildaten nur in der konfigurierten eigenen Datenbank:
SQLite in der Einzelinstanz oder PostgreSQL im Scale-Modus. Die AD- oder
LDAP-Quelle bleibt führend für Identität und Login.

## Export

Der Profilexport enthält nur Daten des angemeldeten Profils:

- Profilstammdaten und KeyWars-Einstellungen;
- Tippversuche und Fehlerauswertung;
- Reward-Ledger, Missionen und Erfolge;
- Schwächenbeobachtungen;
- eigene Texte, Sammlungen und deren Zusammenstellung;
- erstellte Challenges, zugehörige Runden, eigene Challenge-Teilnahmen,
  Bindungen und Rundenergebnisse;
- erstellte Live-Räume und eigene Live-Arena-Ergebniszeilen;
- Moderationsvorgänge, die das Profil ausgeführt haben oder deren Inhalt ihm
  gehört.

Der Export enthält eine Versionsnummer und einen Erstellzeitpunkt. Das aktuelle
Format ist Version 3. Interne Wiederholungs- und Idempotenzwerte wie
`TypingAttempt.Nonce`, `ChallengeAttemptBinding.BindingToken` und
`LiveRoomSummary.IdempotencyKey` werden nicht ausgegeben. Ein automatisierter
Inventartest erzwingt bei neuen Datenbanktabellen eine bewusste
Exportentscheidung.

## Statistik Zurücksetzen

Der Reset muss im Formular mit dem aktuellen AD-/LDAP-Kontonamen bestätigt
werden. Eine falsche Eingabe bricht die Aktion serverseitig ab und lässt die
Statistiken unverändert.

Der Statistik-Reset ist transaktional. Er entfernt Tippversuche, Reward-Ledger,
Missionen, Erfolge und Schwächenbeobachtungen. XP, Level, Serie, Saisonpunkte,
Arena-Rating und gewertete Matchanzahl werden auf Startwerte gesetzt.

AD-Identität, Profilangaben, eigene Texte und Sammlungen bleiben erhalten.
Vor dem Reset sperrt KeyWars neue Profilaktionen, wartet auf bereits laufende
Aktionen, bricht aktive Tippversuche ab, entfernt das Profil aus Live-Räumen und
wartet auf zugehörige Arena-Persistenz. Ein nicht sicher abschließbarer Arena-Job
bricht den Statistik-Reset mit einem wiederholbaren Konflikt ab.

## Profil Löschen

Die Löschung muss im Formular mit dem aktuellen AD-/LDAP-Kontonamen bestätigt
werden. Eine falsche Eingabe bricht die Aktion serverseitig ab; das Profil
bleibt aktiv und die Sitzung bleibt bestehen.

Die Profil-Löschung pseudonymisiert das lokale Profil und meldet die Sitzung
ab. Directory-Identifier, Namen, E-Mail, Abteilung, Titel und Motto werden
entfernt oder durch einen gelöschten Profilbezeichner ersetzt. Ranglisten-,
Ghost- und Challenge-Freigaben werden deaktiviert.

Wie beim Statistik-Reset werden zuerst laufende Profilaktionen, Tippversuche,
Live-Räume und Arena-Persistenz synchronisiert. Erst danach beginnt die
Pseudonymisierungstransaktion. Nach erfolgreicher Löschung sperren sowohl ein
prozesslokaler Tombstone als auch das persistierte `Deleted`-Merkmal alte
Sitzungen.

Private Texte werden geleert und eigene Sammlungen entfernt. Aktive
Challenge-Teilnahmen werden abgelehnt. Aktive Live-Arena-Teilnahmen werden aus
dem laufenden In-Memory-Raum entfernt: vor dem Start als verlassen, während
eines Rennens als nicht beendet.

Historische Gruppen- und Arena-Ergebnisse bleiben zur Integrität der
gemeinsamen Ergebnislisten erhalten, zeigen aber nur noch das gelöschte Profil.
Auch die append-only Moderations-Auditspur bleibt aus Nachweisgründen erhalten.
Ein späterer AD-Login mit derselben Directory-Identität erzeugt ein neues
KeyWars-Profil und wird nicht mit dem gelöschten Profil verknüpft.
