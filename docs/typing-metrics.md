# Tippmetriken und Abschlussvertrag

KeyWars normalisiert Ziel- und Eingabetext kanonisch und wertet beide als
Unicode-Grapheme aus. Dadurch werden unter anderem NFC/NFD-Schreibweisen,
kombinierende Zeichen und Emoji-ZWJ-Sequenzen fachlich konsistent behandelt.
Zeilenenden werden vor der Auswertung vereinheitlicht. Ein
Levenshtein-Alignment ordnet Treffer, Ersetzungen, Einfügungen und Auslassungen
zu. Nicht getippter Zieltext am Ende eines unvollständigen Textversuchs wird
nicht als Fehler gezählt.

## Serverautoritatives Timing

- Der Trainingsmodus bestimmt Dauer und Abschlussregeln. Der mitgesendete
  Kompatibilitätswert `SprintSeconds` ist keine Zeitautorität.
- `Prepare` erzeugt Versuch, Zieltext, Nonce und Text-Hash. Erst `Begin` setzt
  die serverseitige Startzeit und bei Sprintmodi die serverseitige Deadline.
- Ein vollständiger, fehlerfreier Zieltext darf vor der Deadline abgeschlossen
  werden.
- Ein partieller Sprint bleibt bis zur Deadline unverändert aktiv. Ein zu
  früher Abschluss liefert HTTP 409 mit dem stabilen Code
  `attempt_still_running` und einer serverseitigen Restzeit in
  `retryAfterMs`.
- Nach der Deadline darf ein partieller Sprint abgeschlossen werden. Seine
  gewertete Dauer wird auf das Moduslimit begrenzt.
- Ein wiederholter oder paralleler Finish-Request liefert das bereits
  persistierte kanonische Ergebnis. Er erzeugt keine zweite Ledger-, XP- oder
  Challenge-Wirkung.
- Nach einem Prozessneustart werden persistierte, nicht abgeschlossene
  Prepared-/Started-Versuche neutral abgebrochen. Eingaben werden nicht aus
  Clientdaten rekonstruiert.

## Formeln

- `CorrectCharacters`: Anzahl korrekt zugeordneter Grapheme.
- `IncorrectCharacters`: Anzahl echter Ersetzungen, Einfügungen und
  Auslassungen im getippten Bereich.
- `Accuracy`: `CorrectCharacters / (CorrectCharacters + IncorrectCharacters)`.
- `Wpm`: `CorrectCharacters / 5 / Minuten`.
- `RawWpm`: `Eingabegrapheme / 5 / Minuten`.
- `CharactersPerMinute`: `CorrectCharacters / Minuten`.
- `Consistency`: `100 - Variationskoeffizient der abgeschlossenen Wortdauern * 100`.

Ohne mindestens zwei Wortdauer-Samples bleibt `Consistency` neutral bei `100`.
Fehler, Backspaces und Fokusverlust beeinflussen diese Kennzahl nicht direkt;
sie bleiben eigene Metriken.

## Fehler- und Schwachendaten

Pro Versuch werden nur aggregierte Fehlerbeobachtungen gespeichert:

- Position im Zieltext;
- Fehlerart `Insertion`, `Deletion` oder `Substitution`;
- erwartetes und tatsächliches Graphem;
- ein betroffenes Zeichen-/Bigramm-Muster.

Vollständige Keystroke-Replays werden nicht gespeichert. Die
Schwächenanalyse aktualisiert nur Muster aus tatsächlichen Fehlern.
