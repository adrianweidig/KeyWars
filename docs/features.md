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
| Training Classic/Woerter | teilweise | Serverseitiger Attempt-Lebenszyklus mit Prepare/Begin/Finish vorhanden; Browser-Eingabe-E2E offen |
| Zeit-Sprints | teilweise | Serverdauer startet beim Begin-Signal; Teiltext nach Zeitablauf zaehlt, Browser-Eingabe-E2E offen |
| Fehleranalyse | teilweise | Alignment, echte Fehleraggregate und timingbasierte Konsistenz vorhanden; Browser-Visual-E2E und Langzeitaggregation offen |
| Textbibliothek | teilweise | strikter UTF-8-Import und CRUD KW-041 offen |
| Challenges | teilweise | Servergebundenes Challenge-Attempt-Binding, Annahme/Ablehnung, Expiry- und Replaytests vorhanden; Best-of/Browser-E2E offen |
| Live-Arena Lobby | teilweise | Phasen, Countdown, Presence und Hosttransfer begonnen |
| Live-Arena Countdown | teilweise | Serverzeit vorhanden, Browser-E2E fehlt |
| Live-Arena grafische Strecke | geplant | KW-023 |
| Live-HUD und Podium | geplant | KW-024, KW-025 |
| Mehrere Runden/Best-of | geplant | Raumvertrag lehnt Serien bis zur KW-018-Implementierung ab |
| Zuschauer | geplant | KW-027 |
| XP/Level/Missionen | teilweise | Idempotentes Reward-Ledger, stabile Tages-/Wochenmissionen, Level-Fortschritt und 30+ Achievement-Definitionen vorhanden; vollstaendige Achievement-UI/E2E und alle Auditkriterien noch offen |
| Arena-Rating | teilweise | transaktionale Persistenz KW-016/KW-034 offen |
| Profiltrends und Kalender | geplant | KW-033 |
| Datenschutz-Reset/Loeschung | teilweise | Export/Reset/Loeschung mit Re-Provisioning getestet, aktive Browser-E2E offen |
| GHCR Multiarch Image | teilweise | Workflow vorhanden, Release-Gate offen |
| Air-Gap Image-Archiv | teilweise | amd64-Archiv vorhanden, Import-Evidenz offen |
| Playwright Visual Regression | geplant | KW-051 |
| SignalR-Lasttest | teilweise | In-process-Lasttest bis 100 Teilnehmende, echte Netzwerk-Last offen |
