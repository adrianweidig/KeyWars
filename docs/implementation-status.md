# KeyWars Arena & Gamification v1 Status

Dieser Tracker folgt dem Audit-Paket `2f3379490dcd`.

| Issue | Status | PR | Testevidenz | Restpunkte |
| --- | --- | --- | --- | --- |
| KW-000 | teilweise | offen | `scripts/check_implementation_status.py` | Milestone/PR-Struktur fehlt |
| KW-001 | teilweise | offen | `scripts/check_repository_hygiene.py` | Historienrotation dokumentieren |
| KW-002 | erledigt | offen | `docs/configuration.md`, Build/Test gruen | keine |
| KW-003 | teilweise | offen | Build/Test gruen | Browser-E2E und Container-Smoke fehlen |
| KW-010 | teilweise | offen | Concurrency-Tests | Zustandsmaschine vertiefen |
| KW-011 | teilweise | offen | Offizieller lokaler SignalR-Client, Build/Test gruen | Reconnect-E2E fehlt |
| KW-012 | teilweise | offen | Concurrency-Tests fuer Presence, Limit, Raumwechsel, Hosttransfer und Grace-Sweep | Zwei-Tab-Browser-E2E und Presence-Deltas fehlen |
| KW-013 | teilweise | offen | Concurrency-Tests | Zwei-Browser-E2E fehlt |
| KW-014 | teilweise | offen | Concurrency-Tests | Sequenz-/Fuzztests fehlen |
| KW-015 | offen | offen | keine | Delta-/Queue-Protokoll fehlt |
| KW-016 | teilweise | offen | Integrationstests fuer Completion-Queue, Idempotenz, transienten SQLite-Retry, Shutdown-Flush, Serverabbruch ohne Rating und konkurrierende Raeume; Concurrency-Tests fuer einmaliges Enqueue und Cleanup | Mehrfachrunden-/Serienpersistenz und vollstaendige Browser-E2E-Abnahme fehlen |
| KW-017 | teilweise | offen | Build gruen | Fehlercodes/E2E fehlen |
| KW-018 | offen | offen | keine | Serienlogik fehlt |
| KW-020 | offen | offen | keine | Designsystem fehlt |
| KW-021 | offen | offen | keine | Dashboard-Slice fehlt |
| KW-022 | offen | offen | keine | Lobby-Slice fehlt |
| KW-023 | offen | offen | keine | Rennstrecke fehlt |
| KW-024 | offen | offen | keine | HUD/Deltas fehlen |
| KW-025 | offen | offen | keine | Podium/Belohnung fehlt |
| KW-026 | teilweise | offen | CSS Reduced Motion | Sound/Reaktionen fehlen |
| KW-027 | offen | offen | keine | 64er-Darstellung fehlt |
| KW-030 | teilweise | offen | Integrationstest fuer Session-Finish, Unit-Test fuer Sprint-Finish | Persistente Attempt-Phasen fehlen |
| KW-031 | teilweise | offen | Unit-Tests fuer Grapheme und Sprintmodus | Detail-Alignment/Metriken fehlen |
| KW-032 | teilweise | offen | Integrationstest fuer Missions-XP | Reward-Ledger fehlt |
| KW-033 | offen | offen | keine | Profilaggregation fehlt |
| KW-034 | offen | offen | keine | Saisonmodell fehlt |
| KW-040 | teilweise | offen | Integrationstests fuer Ablauf und Attempt-Binding | Best-of/mehrere Runden fehlen |
| KW-041 | teilweise | offen | Integrationstest fuer striktes UTF-8 | Edit/Delete/Import-UX fehlen |
| KW-042 | teilweise | offen | `DisplayNames.For(TrainingMode)` und Profilansichten | Vollstaendige UI-Pruefung fehlt |
| KW-043 | teilweise | offen | Integrationstests fuer Export, Reset, Loeschung und Re-Provisioning | Browser-E2E und vollstaendige Datenschutzabnahme fehlen |
| KW-050 | blockiert | offen | Agent-Share-Pruefung | Kein AD-Runbook gefunden |
| KW-051 | blockiert | offen | HTTP-E2E gruen, Playwright-CLI-Versuch dokumentiert | Lokales Node 8/unvollstaendige Playwright-Runtime |
| KW-052 | teilweise | offen | `tools/KeyWars.LoadTest` lokal bis 100 Teilnehmende | Echter SignalR-Netzlasttest fehlt |
| KW-053 | teilweise | offen | Rate-Limits, CSP, LDAP-CA/Timeout-Tests | Real-AD-Verifikation fehlt |
| KW-054 | blockiert | offen | Workflow-Review | Docker lokal nicht verfuegbar |
| KW-055 | teilweise | offen | Release-Workflow mit Hygiene-, Status-, Test- und Load-Gates | Realer Tag/GHCR-Release fehlt |
