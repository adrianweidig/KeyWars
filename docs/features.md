# Feature-Matrix

Statuswerte:

- `verfuegbar`: produktiver Codepfad ist vorhanden und getestet.
- `teilweise`: nutzbarer Kern ist vorhanden, aber Abnahme aus dem Audit fehlt.
- `geplant`: im Audit-Paket spezifiziert, aber noch nicht produktiv umgesetzt.
- `blockiert`: benoetigt externe Evidenz oder Zugang.

| Bereich | Status | Hinweise |
| --- | --- | --- |
| LDAP/LDAPS-Login | teilweise | Real-AD-E2E ist KW-050 und derzeit blockiert |
| JIT-Provisionierung | verfuegbar | per Directory-GUID getestet |
| Lokaler Development-Login | verfuegbar | nur in `Development` |
| Training Classic/Woerter | teilweise | Attempt-Lebenszyklus KW-030 offen |
| Zeit-Sprints | teilweise | Serverabschlussregeln KW-030 offen |
| Fehleranalyse | teilweise | Alignment und echte Fehleraggregate KW-031 offen |
| Textbibliothek | teilweise | strikter UTF-8-Import und CRUD KW-041 offen |
| Challenges | teilweise | Challenge-Attempt-Binding KW-040 offen |
| Live-Arena Lobby | teilweise | Phasen/Countdown begonnen |
| Live-Arena Countdown | teilweise | Serverzeit vorhanden, Browser-E2E fehlt |
| Live-Arena grafische Strecke | geplant | KW-023 |
| Live-HUD und Podium | geplant | KW-024, KW-025 |
| Mehrere Runden/Best-of | geplant | KW-018 |
| Zuschauer | geplant | KW-027 |
| XP/Level/Missionen | teilweise | Reward-Ledger KW-032 offen |
| Arena-Rating | teilweise | transaktionale Persistenz KW-016/KW-034 offen |
| Profiltrends und Kalender | geplant | KW-033 |
| Datenschutz-Reset/Loeschung | teilweise | Re-Provisioning-Semantik KW-043 offen |
| GHCR Multiarch Image | teilweise | Workflow vorhanden, Release-Gate offen |
| Air-Gap Image-Archiv | teilweise | amd64-Archiv vorhanden, Import-Evidenz offen |
| Playwright Visual Regression | geplant | KW-051 |
| SignalR-Lasttest | geplant | KW-052 |
