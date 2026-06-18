# ADR 0003: JIT-Provisionierung statt AD-Sync

## Kontext

Ohne Servicekonto darf KeyWars nicht das gesamte Verzeichnis importieren.

## Entscheidung

Profile entstehen beim ersten erfolgreichen Login anhand `objectGUID`.

## Konsequenzen

Personensuche findet nur lokal bekannte Profile.
