# KeyWars Arena & Gamification v1 Status

Dieser Tracker folgt dem Audit-Paket `2f3379490dcd`.

| Issue | Status | PR | Testevidenz | Restpunkte |
| --- | --- | --- | --- | --- |
| KW-000 | teilweise | offen | `scripts/check_implementation_status.py` | Milestone/PR-Struktur fehlt |
| KW-001 | teilweise | offen | `scripts/check_repository_hygiene.py` | Historienrotation dokumentieren |
| KW-002 | erledigt | offen | `docs/configuration.md`, Build/Test gruen | keine |
| KW-003 | teilweise | offen | Build/Test gruen | Browser-E2E und Container-Smoke fehlen |
| KW-010 | teilweise | offen | Concurrency-Tests fuer Lobby-Vertrag, idempotenten Start, parallelen Start, einmaligen Countdown-zu-Running-Uebergang, Serienabschluss und Serverabbruch ohne doppelte Persistenz | Rollen-/Fehlerpfad-Matrix, Hub-/Browser-E2E und Fuzzer fuer erlaubte Zustandsuebergaenge fehlen |
| KW-011 | teilweise | offen | Offizieller lokaler SignalR-Client, Build/Test gruen | Reconnect-E2E fehlt |
| KW-012 | teilweise | offen | Concurrency-Tests fuer Presence, Limit, Raumwechsel, Hosttransfer und Grace-Sweep | Zwei-Tab-Browser-E2E und Presence-Deltas fehlen |
| KW-013 | teilweise | offen | Concurrency-Tests | Zwei-Browser-E2E fehlt |
| KW-014 | teilweise | offen | Concurrency-Tests fuer Backspace-Fortschritt, falschen Finish ohne DNF, expliziten GiveUp-DNF, NFC-Grapheme, Oversize-Ablehnung und alte Sequenzen; Build/Test gruen | Frequenz-/Sprungheuristiken, echter Hub-/Browser-E2E und umfassende Fuzztests fehlen |
| KW-015 | teilweise | offen | Unit-Tests fuer Progress-Koaleszierung und Pending-Drop; Concurrency-Test fuer Delta-Pfad ohne Vollsnapshot; Build/Test gruen | Vollstaendige RoomCommand-Pipeline, echter SignalR-Netzlasttest und Soak-Evidenz fehlen |
| KW-016 | teilweise | offen | Integrationstests fuer Completion-Queue, Idempotenz, transienten SQLite-Retry, Shutdown-Flush, Serverabbruch ohne Rating und konkurrierende Raeume; Concurrency-Tests fuer einmaliges Enqueue und Cleanup | Mehrfachrunden-/Serienpersistenz und vollstaendige Browser-E2E-Abnahme fehlen |
| KW-017 | teilweise | offen | Unit-Tests fuer Raumcodevalidierung; HTTP-Smoke fuer Redirect der alten `/arena/{id}/rennen`-Route und kanonische Seite ohne manuellen Finish-Fallback; Build/Test gruen; DNF-Aktion und differenzierte Raumfehler umgesetzt | Rollenbasierte Browser-E2E, Copy-to-Clipboard und Slow-network-/Double-click-Evidenz fehlen |
| KW-018 | teilweise | offen | Concurrency-Tests fuer Einzelrundenvertrag und Ablehnung von 3/5-Runden-Serien; Build/Test gruen | Serienlogik, Best-of, Revanche und Browser-E2E fehlen |
| KW-020 | offen | offen | keine | Designsystem fehlt |
| KW-021 | offen | offen | keine | Dashboard-Slice fehlt |
| KW-022 | offen | offen | keine | Lobby-Slice fehlt |
| KW-023 | offen | offen | keine | Rennstrecke fehlt |
| KW-024 | offen | offen | keine | HUD/Deltas fehlen |
| KW-025 | offen | offen | keine | Podium/Belohnung fehlt |
| KW-026 | teilweise | offen | CSS Reduced Motion | Sound/Reaktionen fehlen |
| KW-027 | offen | offen | keine | 64er-Darstellung fehlt |
| KW-030 | teilweise | offen | Integrationstests fuer Prepared/Started/Finished/Expired, Nonce-Replay, Sprint-Teilabschluss, Session-Finish; HTTP-E2E und Build/Test gruen | Browser-E2E mit echter Eingabe/Fokusverlust und Abbruch-UX fehlen |
| KW-031 | teilweise | offen | Golden-Master-Unit-Tests fuer Alignment/Formeln/Konsistenz; Integrationstest fuer persistierte Fehler, Wortzeitaggregate und echte Schwaechenmuster; Build/Test gruen | Langzeitgewichtung, Browser-Visual-E2E und vollstaendige Resultatseiten-Abnahme fehlen |
| KW-032 | teilweise | offen | Integrationstests fuer Reward-Ledger-Idempotenz, stabile Mission-Keys, Tages-/Wochenmissionen, Arena-XP, Achievement-Definitionen und Ultrakurz-Farm-Schutz; Build/Test gruen | Vollstaendige Achievement-Auswertung fuer alle Kategorien, Wochen-/Team-UI, Browser-E2E fuer sichtbaren XP-/Levelanstieg und alle Auditkriterien fehlen |
| KW-033 | offen | offen | keine | Profilaggregation fehlt |
| KW-034 | offen | offen | keine | Saisonmodell fehlt |
| KW-040 | teilweise | offen | Integrationstests fuer gebundenen Challenge-Start, freie-Attempt-Manipulation, Wiederverwendung, Expiry und Annahmepflicht; Build/Test gruen | Best-of/mehrere Runden, Browser-E2E und echte Parallelabschluss-Evidenz fehlen |
| KW-041 | teilweise | offen | Integrationstests fuer striktes UTF-8, NFC-/Limitvalidierung, unsichtbare/Steuerzeichen, Ownership bei Edit/Delete, POST-Kopie, Sammlungsschutz und Filter/Paging; Build/Test gruen | Browser-E2E fuer CRUD/Upload/Sammlung, echte Pagination-UI und vollstaendige Direktaktionen fehlen |
| KW-042 | teilweise | offen | `DisplayNames.For(TrainingMode)` und Profilansichten | Vollstaendige UI-Pruefung fehlt |
| KW-043 | teilweise | offen | Integrationstests fuer Export, Reset, Loeschung und Re-Provisioning | Browser-E2E und vollstaendige Datenschutzabnahme fehlen |
| KW-050 | blockiert | offen | Agent-Share-Pruefung | Kein AD-Runbook gefunden |
| KW-051 | blockiert | offen | HTTP-E2E gruen, Playwright-CLI-Versuch dokumentiert | Lokales Node 8/unvollstaendige Playwright-Runtime |
| KW-052 | teilweise | offen | `tools/KeyWars.LoadTest` lokal bis 100 Teilnehmende | Echter SignalR-Netzlasttest fehlt |
| KW-053 | teilweise | offen | Rate-Limits, CSP, LDAP-CA/Timeout-Tests | Real-AD-Verifikation fehlt |
| KW-054 | blockiert | offen | Workflow-Review | Docker lokal nicht verfuegbar |
| KW-055 | teilweise | offen | Release-Workflow mit Hygiene-, Status-, Test- und Load-Gates | Realer Tag/GHCR-Release fehlt |
