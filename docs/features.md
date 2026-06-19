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
| Dashboard | teilweise | Tagesfokus, Mission-Fortschritt, 30-Tage-Aktivitaet und lokalisierte Challenge-Statusnamen vorhanden; Visual-/Fehlerzustandsabnahme offen |
| Textbibliothek | teilweise | strikter UTF-8-Import, NFC-/Limitvalidierung, Suche/Filter, POST-Kopie, Edit/Delete mit Ownership- und Referenzschutz vorhanden; Browser-E2E/Pagination-Abnahme offen |
| Challenges | teilweise | Servergebundenes Challenge-Attempt-Binding, Annahme/Ablehnung, Expiry- und Replaytests vorhanden; Best-of/Browser-E2E offen |
| Live-Arena Lobby | teilweise | Phasen, Countdown, Presence, Hosttransfer, nutzerorientierte Einstiegspfade und konfigurierbare Raumkapazitaet begonnen |
| Live-Arena Countdown | teilweise | Serverzeit vorhanden, Browser-E2E fehlt |
| Live-Arena grafische Strecke | teilweise | DOM-basierte Rennstrecke mit Meilensteinen, eigener Spur und CSS-Transform-Fortschritt vorhanden; Visual-/Performance-Evidenz offen |
| Live-Arena adaptive Grossraumansicht | teilweise | 2-8 Detailansicht, 9-24 kompakte Ansicht und ab 25 fokussiertes Fenster mit Top-Plaetzen, eigener Position und Nachbarn vorhanden; 64er Visual-/Performance-Evidenz offen |
| Live-HUD und Podium | teilweise | Persoenliches HUD, Ranglisten-Updates, Live-Region und Podium-Container vorhanden; echte Ueberhol-/Belohnungs-E2E offen |
| Motion, Sound und Reaktionen | teilweise | Profilsettings, synthetische Opt-in-Sounds nach Nutzerinteraktion, Motion-Tokens und feste serverseitig begrenzte Arena-Reaktionen vorhanden; vollstaendige Browser-A11y-/Soundabnahme offen |
| Einstellungen und Lokalisierung | teilweise | de-DE ist als RequestCulture gesetzt, Domain-Enums haben getestete deutsche DisplayNames und Einstellungen sind nach Darstellung, Training, Arena sowie Profil/Privatsphaere gruppiert; Fehler-UX-/Pluralisierungsabnahme offen |
| Mehrere Runden/Best-of | geplant | Raumvertrag lehnt Serien bis zur KW-018-Implementierung ab |
| Zuschauer | geplant | KW-027 bereitet die Anzeige vor; produktive Zuschauerrolle, Berechtigungen und Update-Priorisierung fehlen |
| XP/Level/Missionen | teilweise | Idempotentes Reward-Ledger, stabile Tages-/Wochenmissionen, Level-Fortschritt und 30+ Achievement-Definitionen vorhanden; vollstaendige Achievement-UI/E2E und alle Auditkriterien noch offen |
| Arena-Rating | teilweise | Transaktionale Persistenz mit RatingBefore/Delta/After fuer Arena-Ergebnisse vorhanden; Saisonmodell, Rivalen und vollstaendige Ranking-Abnahme offen |
| Profiltrends und Kalender | teilweise | SQL-aggregierte 7/30/90-Tage-Trends, Aktivitaetskalender, Bestwerte und paginierte Historie vorhanden; Visual-/Accessibility-Abnahme offen |
| Datenschutz-Reset/Loeschung | teilweise | Export/Reset/Loeschung mit Re-Provisioning getestet, aktive Browser-E2E offen |
| GHCR Multiarch Image | teilweise | Workflow vorhanden, Release-Gate offen |
| Air-Gap Image-Archiv | teilweise | amd64-Archiv vorhanden, Import-Evidenz offen |
| Playwright Visual Regression | geplant | KW-051 |
| SignalR-Lasttest | teilweise | In-process-Lasttest bis 100 Teilnehmende, echte Netzwerk-Last offen |
