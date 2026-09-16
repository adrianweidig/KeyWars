# Implementierungsstatus

Referenzstand ist der `v0.5`-Codepfad. Die Matrix trennt vorhandene
Repository-Capabilities von noch offenen Betreiber- oder Langzeitabnahmen. Die
kompakte Produktsicht steht in [features.md](features.md). Dies ist die einzige
Statusmatrix; Prüfskripte und Workflows bleiben die maßgebliche Evidenz.

| Prüfpunkt | Status | Capability und Evidenz |
| --- | --- | --- |
| KW-000 | teilweise | `scripts/check_implementation_status.py` prüft alle erwarteten IDs und Statuswerte; inhaltliche Evidenz und Projektsteuerung bleiben getrennte Abnahmen. |
| KW-001 | teilweise | Getrackte Repository-Hygiene wird automatisiert geprüft; sauberer Arbeitsbaum und historische Betriebsartefakte bleiben Release- beziehungsweise Betreiber-Gates. |
| KW-002 | erledigt | Konfigurationsbindung, Startvalidierung und Referenzdokumentation sind vorhanden. |
| KW-003 | teilweise | Protokoll v2 verteilt Attempt-, Presence-, Progress-, Completion- und Profilzugriffszustand auf 256 Buckets; Raumzustand nutzt je Raum einen eigenen Hash-Tag. Unit-/Concurrency-Verträge und ein Sechs-Knoten-Harness decken begrenzte Restarts, Failover und Rolling Updates ab; lange Partitionstests und Kapazitätsnachweise bleiben extern. |
| KW-010 | teilweise | Arena-Zustandsübergänge und idempotenter Start sind concurrency-getestet; Rollen- und Fuzzmatrix bleibt offen. |
| KW-011 | teilweise | SignalR-Client, Zwei-Browser-Fluss, Reconnect und Persistenzstatus sind getestet; Langzeitfehler bleiben offen. |
| KW-012 | teilweise | Presence, Limits, Raumwechsel, Hosttransfer und gebroadcasteter Grace-Sweep sind concurrency- und browsergetestet; Mehrtab-Matrix bleibt offen. |
| KW-013 | teilweise | Deltaübertragung sowie Zwei- und Vier-Browser-Flüsse sind getestet; Mehrraum- und Langzeitevidenz bleibt offen. |
| KW-014 | teilweise | Graphemfortschritt, Reihenfolge und Eingabegrenzen sind getestet; breites Fuzzing bleibt offen. |
| KW-015 | teilweise | Begrenzte Progress-Pipeline mit Koaleszierung und Drop-Zählern ist getestet; der Cluster-Harness enthält einen begrenzten Soak, belastbare Langzeit- und Ressourcenprofile bleiben extern. |
| KW-016 | teilweise | Idempotente Completion-Queue mit Retry, Drain, Recovery und Statusmodell ist getestet; begrenzte Restart-/Failover-Fälle sind automatisiert, lange Fault-Injection bleibt extern. |
| KW-017 | teilweise | Kanonische Raumroute, Teilen, DNF und Submit-Guards sind getestet; breite Gerätematrix bleibt offen. |
| KW-018 | teilweise | Serien- und Teamwertung, Hostübergabe sowie eine hostgebundene, idempotente Arena-Revanche sind implementiert und auf Serviceebene getestet; die neue Browser-Spec bleibt bis zum finalen UI-Gate unbestätigt. |
| KW-020 | teilweise | App-Rahmen, Themes und Kernkomponenten sind HTTP-getestet; vollständige Komponenten- und Visualmatrix bleibt offen. |
| KW-021 | teilweise | Dashboard-Aggregate, Tagesfokus und Challenge-Status sind getestet; visuelle Fehlerzustände bleiben offen. |
| KW-022 | teilweise | Lobby-Einstiege, Kapazität, Teilen und Submit-Guard sind getestet; Vollraum- und Live-Update-Abnahme bleibt offen. |
| KW-023 | teilweise | Rennstrecke, Meilensteine und Reduced Motion sind vorhanden; Großraum-Visualprofil bleibt offen. |
| KW-024 | teilweise | HUD, Rangliste und Live-Region folgen bestätigten Serverdaten; Überhol- und Reconnect-Matrix bleibt offen. |
| KW-025 | erledigt | Podium trennt vorläufiges Ergebnis und Commitstatus; Revanche ist hostgebunden und idempotent. Persönliche Bestwerte werden erst nach `Persisted` für denselben Zieltext-Hash und Modus bestätigt; Integration-, HTTP- und fokussierter Browserfall decken Erfolg und Fehlerzustände ab. |
| KW-026 | teilweise | Axe, Tastatur, Themes, Reduced Motion, Mobilansicht und Reflow sind automatisiert; NVDA und echtes Gerät bleiben offen. |
| KW-027 | teilweise | Vier isolierte Browserkontexte, 2-vs-2-Wertung, Mobilansicht und Last-Smoke sind geprüft; 64er-Visualabnahme bleibt offen. |
| KW-030 | teilweise | Prepare/Begin/Finish, Serverfrist und kanonischer Retry sind getestet; breite Abbruchmatrix bleibt offen. |
| KW-031 | teilweise | Speicherbegrenztes exaktes Alignment, Formeln und persistierte Fehleraggregate sind getestet; Langzeitgewichtung bleibt offen. |
| KW-032 | teilweise | Reward-Ledger, Missionen, Arena-XP, Farm-Schutz sowie der Erfolge-Katalog sind getestet; quantifizierter Fortschritt zu gesperrten Erfolgen bleibt offen. |
| KW-033 | teilweise | SQL-aggregierte Trends, Bestwerte und paginierte Historie unterstützen 7, 30 und 90 Tage; Integrationstests sind vorhanden, die neue Browser-Spec bleibt bis zum finalen UI-Gate unbestätigt. |
| KW-034 | teilweise | Elo, Auditwerte, persistente Saisonwertung mit geleastem Rollover sowie datensparsame Rivalen-Geister sind fokussiert getestet; produktiver Saisonwechsel und Mehr-Replica-Rollover bleiben externe Abnahmen. |
| KW-040 | teilweise | Gebundene Challenge-Versuche, Best-of, Abbruch und idempotente Revanche sind integrations- und browsergetestet; lange Fehler- und Mehrgeräteabnahmen bleiben offen. |
| KW-041 | teilweise | UTF-8, NFC, Limits, Manipulationsschutz, Ownership sowie Text- und Sammlungs-CRUD sind integrationsgetestet. Eine Text-CRUD-Browser-Spec ist vorhanden; Sammlungs-CRUD und das finale UI-Gate bleiben offen. |
| KW-042 | teilweise | de-DE, Enum-Anzeigenamen, Einstellungen und Mojibake-Hygiene sind getestet; UX- und Pluralmatrix bleibt offen. |
| KW-043 | teilweise | Profil-Gate, Drain, Tombstone, Re-Provisionierung und der Profil-zu-Raum-Reverse-Index sind implementiert und getestet; produktive Zwei-Browser-, Crash- und Langzeitabnahmen bleiben extern. |
| KW-050 | erledigt | Real-LDAPS deckt Fehlerkonten, zwei echte Logins und einen Arena-Fluss ab; Netzdetails bleiben privat. |
| KW-051 | teilweise | Playwright sowie ein aktiver FlaUI-/UIA3-/OpenCV-Lauf decken Kernflüsse, Breakpoints, Reflow, Axe und Tastatur ab; Geräte- und Screenreader-Matrix bleibt offen. |
| KW-052 | teilweise | In-Process- und SignalR-Lasttests sowie Mehr-Replica-Smoke sind reproduzierbar; der Sechs-Knoten-Harness automatisiert begrenzten Soak, Restart, Redis-Failover und Rolling Update. Lange Kapazitäts- und Ressourcenprofile bleiben extern. |
| KW-053 | teilweise | Rate-Limits, Sicherheitsheader, Proxy-Vertrauen und Production-Fail-Closed sind getestet; der Real-LDAPS-Refresh für den neuen Release ist eine externe Release-Abnahme, keine offene Code-Capability. |
| KW-054 | erledigt | Die Releasepipeline erzeugt Compose/env, Offline-Archiv, Manifest, Prüfsummen und ein Multiarch-GHCR-Image mit OCI-Metadaten. |
| KW-055 | erledigt | Release-, Qualitäts-, Windows-UI- und Sicherheitsworkflows bilden die veröffentlichten Gates. |
