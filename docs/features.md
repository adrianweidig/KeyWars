# Funktionsumfang

KeyWars verbindet individuelles Tipptraining mit Challenges und Live-Rennen.
Die Anwendung nutzt vorhandene AD-/LDAP-Identitäten; eine separate lokale
Nutzerverwaltung ist nicht vorgesehen.

## Training und Texte

- klassische Textläufe, Wörtertests und zeitlich begrenzte Sprints;
- Fehlerfokus, eigener Geist und ausdrücklich freigegebene Rivalen-Geister
  ohne Tasten-Replay;
- kuratierte Standardtexte und eine eigene Textbibliothek mit Suche, Filtern,
  Import, Kopie und Bewertung;
- Dashboard, Missionen, Erfolge, Serien und persönliche Trends für 7, 30 oder
  90 Tage.

## Gemeinsam spielen

- Challenges mit Einladung, Annahme, Ablauf und servergebundenen Versuchen;
- Live-Arena mit Raumcode, Lobby, Countdown, Rennen und Podium;
- Einzelrennen, Drei- oder Fünf-Runden-Serien und automatisch ausgeglichene
  Teams;
- hostgebundene Arena-Revanche und persönliche Bestwerte nach bestätigter
  Persistenz;
- transiente Live-Vorschau ohne persistierte Tasten- oder Replaydaten.

## Profil und Datenschutz

- JIT-Profilanlage nach erfolgreicher Verzeichnisanmeldung;
- persönliche Ziele, Darstellungs- und Arenaeinstellungen;
- Selbstauskunft, Datenexport, Statistik-Reset und Profillöschung;
- getrennte Freigaben für Ranglistenanzeige und Rivalen-Geister.

## Motivation und Wettbewerb

- XP, Level, Missionen, Erfolge, Serien sowie Arena- und Challenge-Rating;
- konfigurierbare UTC-Saisons mit eigener Punktewertung und Rangliste;
- keine Währung, kein Shop und keine künstliche Knappheit.

## Betrieb

- einfache Einzelinstanz mit `compose.yaml` und SQLite;
- optionaler Scale-Modus mit getrennten Rollen, PostgreSQL und Redis;
- Health-Endpunkte, Online-Backups der SQLite-Einzelinstanz, dokumentierte
  PostgreSQL-Sicherung im Scale-Modus, Retention, GHCR-Image und Air-Gap-Artefakte;
- Browser-, Integrations-, Concurrency-, Last- und Windows-UI-Tests.

Bewusst nicht enthalten sind lokale Produktionskonten, ein externer CDN-Zwang
und die Speicherung von Tastenfolgen. Eine Zuschauerrolle und eine garantierte
Clustergröße sind keine zugesicherten Funktionen.

Der technische Nachweis einzelner Fähigkeiten und offene Abnahmen stehen nur
im [Implementierungsstatus](implementation-status.md).
