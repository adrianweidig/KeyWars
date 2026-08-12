# Implementierungsstatus

Referenzstand ist der `v0.5`-Codepfad. Die Matrix trennt vorhandene
Repository-Capabilities von noch offenen Betreiber- oder Langzeitabnahmen. Die
kompakte Produktsicht steht in [features.md](features.md). Dies ist die einzige
Statusmatrix; Prüfskripte und Workflows bleiben die maßgebliche Evidenz.

| Prüfpunkt | Status | Capability und Evidenz |
| --- | --- | --- |
| KW-000 | teilweise | Vollständige Audit-ID-Matrix wird durch `scripts/check_implementation_status.py` geprüft; Projektsteuerung bleibt extern. |
| KW-001 | teilweise | Repository-Hygiene wird automatisiert geprüft; Rotation historischer Betriebsartefakte bleibt Betreiberaufgabe. |
| KW-002 | erledigt | Konfigurationsbindung, Startvalidierung und Referenzdokumentation sind vorhanden. |
| KW-003 | teilweise | Einzelinstanz und Scale-Modus besitzen explizite Datenbank-, Rollen- und Wartungsverträge; ein CI-Smoke prüft zwei Web- und zwei Arena-Replikate samt Arena-Ausfall, lange Cluster- und Partitionstests bleiben offen. |
| KW-010 | teilweise | Arena-Zustandsübergänge und idempotenter Start sind concurrency-getestet; Rollen- und Fuzzmatrix bleibt offen. |
| KW-011 | teilweise | SignalR-Client, Zwei-Browser-Fluss, Reconnect und Persistenzstatus sind getestet; Langzeitfehler bleiben offen. |
| KW-012 | teilweise | Presence, Limits, Raumwechsel, Hosttransfer und gebroadcasteter Grace-Sweep sind concurrency- und browsergetestet; Mehrtab-Matrix bleibt offen. |
| KW-013 | teilweise | Deltaübertragung sowie Zwei- und Vier-Browser-Flüsse sind getestet; Mehrraum- und Langzeitevidenz bleibt offen. |
| KW-014 | teilweise | Graphemfortschritt, Reihenfolge und Eingabegrenzen sind getestet; breites Fuzzing bleibt offen. |
| KW-015 | teilweise | Begrenzte Progress-Pipeline mit Koaleszierung und Drop-Zählern ist getestet; Ressourcenprofil und Soak bleiben offen. |
| KW-016 | teilweise | Idempotente Completion-Queue mit Retry, Drain und Statusmodell ist getestet; lange Fault-Injection bleibt offen. |
| KW-017 | teilweise | Kanonische Raumroute, Teilen, DNF und Submit-Guards sind getestet; breite Gerätematrix bleibt offen. |
| KW-018 | teilweise | Serien- und Teamwertung sowie Hostübergabe zwischen Serienrunden sind concurrency- und browsergetestet; eine eigene Arena-Revancheaktion bleibt offen. |
| KW-020 | teilweise | App-Rahmen, Themes und Kernkomponenten sind HTTP-getestet; vollständige Komponenten- und Visualmatrix bleibt offen. |
| KW-021 | teilweise | Dashboard-Aggregate, Tagesfokus und Challenge-Status sind getestet; visuelle Fehlerzustände bleiben offen. |
| KW-022 | teilweise | Lobby-Einstiege, Kapazität, Teilen und Submit-Guard sind getestet; Vollraum- und Live-Update-Abnahme bleibt offen. |
| KW-023 | teilweise | Rennstrecke, Meilensteine und Reduced Motion sind vorhanden; Großraum-Visualprofil bleibt offen. |
| KW-024 | teilweise | HUD, Rangliste und Live-Region folgen bestätigten Serverdaten; Überhol- und Reconnect-Matrix bleibt offen. |
| KW-025 | teilweise | Podium trennt vorläufiges Ergebnis und Commitstatus; Revanche-, Bestwert- und Visualmatrix bleibt offen. |
| KW-026 | teilweise | Axe, Tastatur, Themes, Reduced Motion, Mobilansicht und Reflow sind automatisiert; NVDA und echtes Gerät bleiben offen. |
| KW-027 | teilweise | Vier isolierte Browserkontexte, 2-vs-2-Wertung, Mobilansicht und Last-Smoke sind geprüft; 64er-Visualabnahme bleibt offen. |
| KW-030 | teilweise | Prepare/Begin/Finish, Serverfrist und kanonischer Retry sind getestet; breite Abbruchmatrix bleibt offen. |
| KW-031 | teilweise | Speicherbegrenztes exaktes Alignment, Formeln und persistierte Fehleraggregate sind getestet; Langzeitgewichtung bleibt offen. |
| KW-032 | teilweise | Reward-Ledger, Missionen, Arena-XP und Farm-Schutz sind getestet; vollständige Achievement-UX bleibt offen. |
| KW-033 | teilweise | Gruppierte 90-Tage-Trends, Bestwerte und paginierte Historie sind getestet; Zeitraumwahl und Visualabnahme bleiben offen. |
| KW-034 | teilweise | Paarweises Elo und transaktionale Auditwerte sind getestet; Saisons und Rivalen bleiben offen. |
| KW-040 | teilweise | Gebundene Challenge-Versuche, Best-of, Abbruch und idempotente Revanche sind integrations- und browsergetestet; lange Fehler- und Mehrgeräteabnahmen bleiben offen. |
| KW-041 | teilweise | UTF-8, NFC, Graphem-/Payload-Limits, Manipulationsschutz, Ownership, Kopie, Filter und Paging sind getestet; Browser-CRUD bleibt offen. |
| KW-042 | teilweise | de-DE, Enum-Anzeigenamen, Einstellungen und Mojibake-Hygiene sind getestet; UX- und Pluralmatrix bleibt offen. |
| KW-043 | teilweise | Profil-Gate, Drain, Tombstone und Re-Provisionierung sind getestet; produktive Zwei-Browser-Abnahme bleibt offen. |
| KW-050 | erledigt | Real-LDAPS deckt Fehlerkonten, zwei echte Logins und einen Arena-Fluss ab; Netzdetails bleiben privat. |
| KW-051 | teilweise | Playwright sowie ein aktiver FlaUI-/UIA3-/OpenCV-Lauf decken Kernflüsse, Breakpoints, Reflow, Axe und Tastatur ab; Geräte- und Screenreader-Matrix bleibt offen. |
| KW-052 | teilweise | In-Process-Lasttest bis 100, ein strikter SignalR-Lauf mit zwei Räumen und je drei Profilen sowie der Mehr-Replica-CI-Smoke sind reproduzierbar; Soak und belastbare Ressourcenprofile bleiben offen. |
| KW-053 | teilweise | Rate-Limits, Sicherheitsheader, Proxy-Vertrauen und Production-Fail-Closed sind getestet; finaler Real-LDAPS-Refresh bleibt offen. |
| KW-054 | erledigt | Die Releasepipeline erzeugt Compose/env, Offline-Archiv, Manifest, Prüfsummen und ein Multiarch-GHCR-Image mit OCI-Metadaten. |
| KW-055 | erledigt | Release-, Qualitäts-, Windows-UI- und Sicherheitsworkflows bilden die veröffentlichten Gates. |
