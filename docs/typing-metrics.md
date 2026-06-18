# Tippmetriken

KeyWars wertet Ziel- und Eingabetext als normalisierte Unicode-Grapheme aus.
Ein Levenshtein-Alignment ordnet Treffer, Ersetzungen, Einfuegungen und
Auslassungen zu. Nicht getippter Zieltext am Ende eines unvollstaendigen
Textversuchs wird nicht als Fehler gezaehlt.

## Formeln

- `CorrectCharacters`: Anzahl korrekt zugeordneter Grapheme.
- `IncorrectCharacters`: Anzahl echter Ersetzungen, Einfuegungen und
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
- erwartetes und tatsaechliches Graphem;
- ein betroffenes Zeichen-/Bigramm-Muster.

Vollstaendige Keystroke-Replays werden nicht gespeichert. Die
Schwaechenanalyse aktualisiert nur Muster aus tatsaechlichen Fehlern.
