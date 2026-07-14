# Feature-Matrix

Statuswerte:

- `verfügbar`: produktiver Codepfad ist vorhanden und getestet.
- `teilweise`: nutzbarer Kern ist vorhanden, aber Abnahme aus dem Audit fehlt.
- `geplant`: im Audit-Paket spezifiziert, aber noch nicht produktiv umgesetzt.
- `blockiert`: benötigt externe Evidenz oder Zugang.

| Bereich | Status | Hinweise |
| --- | --- | --- |
| LDAP/LDAPS-Login | verfügbar | Real-AD-E2E gegen LDAPS-Production-Instanz mit zwei AD-Nutzern, deaktiviertem Konto und DB-Evidenz vorhanden |
| JIT-Provisionierung | verfügbar | per Directory-GUID getestet |
| Lokaler Development-Login | verfügbar | nur in `Development` |
| Training Classic/Wörter | teilweise | Serialisierter Prepare/Begin/Finish-Vertrag, kanonischer Retry und Browser-Eingabe-E2E vorhanden; vollständige Abbruch-UX offen |
| Zeit-Sprints | teilweise | Modus und Frist sind serverautoritativ; vor Frist ist nur vollständig korrekter Zieltext zulässig, sonst typisierter 409-Konflikt; Browser-Retry-E2E vorhanden |
| Fehleranalyse | teilweise | Alignment, echte Fehleraggregate und timingbasierte Konsistenz vorhanden; Browser-Visual-E2E und Langzeitaggregation offen |
| Dashboard | teilweise | Tagesfokus, Mission-Fortschritt, 30-Tage-Aktivität und lokalisierte Challenge-Statusnamen vorhanden; Visual-/Fehlerzustandsabnahme offen |
| Textbibliothek | teilweise | 33 kuratierte Standardtexte einschließlich zwölf militärischer Fachtexte und neun Geschichten, strikter UTF-8-Import, NFC-/Limitvalidierung, Suche/Filter, POST-Kopie, Edit/Delete mit Ownership- und Referenzschutz vorhanden; Browser-E2E/Pagination-Abnahme offen |
| Challenges | teilweise | Servergebundenes Challenge-Attempt-Binding, Annahme/Ablehnung, Expiry- und Replaytests vorhanden; Best-of/Browser-E2E offen |
| Live-Arena Lobby | teilweise | Phasen, Countdown, Presence, Hosttransfer, nutzerorientierte Einstiegspfade und konfigurierbare Raumkapazität begonnen |
| Live-Arena Countdown | teilweise | Serverzeit, kanonischer Start und Zwei-Kontext-Browser-E2E vorhanden; Langzeit-Reconnect offen |
| Live-Arena grafische Strecke | teilweise | DOM-basierte Rennstrecke mit Meilensteinen, eigener Spur und CSS-Transform-Fortschritt vorhanden; Visual-/Performance-Evidenz offen |
| Live-Arena Live-Text | teilweise | 10FastFingers-artiger oberer Tippbereich mit transienten Textpreviews, grüner/roter Zieltextmarkierung und Browser-E2E für Zwei-Nutzer-Fehlerkorrektur vorhanden; Visual-/Performance-Matrix offen |
| Live-Arena adaptive Großraumansicht | teilweise | 2-8 Detailansicht, 9-24 kompakte Ansicht und ab 25 fokussiertes Fenster mit Top-Plätzen, eigener Position und Nachbarn vorhanden; 64er Visual-/Performance-Evidenz offen |
| Live-HUD und Podium | teilweise | Persönliches HUD, Ranglisten-Updates und vorläufiges Podium vorhanden; Persistenzstatus ist getrennt und XP/Rating erscheinen erst nach bestätigtem Commit |
| Motion, Sound und Reaktionen | teilweise | Profilsettings, synthetische Opt-in-Sounds nach Nutzerinteraktion, Motion-Tokens und feste serverseitig begrenzte Arena-Reaktionen vorhanden; vollständige Browser-A11y-/Soundabnahme offen |
| Einstellungen und Lokalisierung | teilweise | de-DE ist als RequestCulture gesetzt, Domain-Enums haben getestete deutsche DisplayNames und Einstellungen sind nach Darstellung, Training, Arena sowie Profil/Privatsphäre gruppiert; Fehler-UX-/Pluralisierungsabnahme offen |
| Serienrennen | verfügbar | Drei oder fünf serverautoritativ geführte Runden, Platzierungspunkte, Rundensiege, Gesamtwertung und einmalige aggregierte Persistenz sind concurrency-getestet |
| Teamwertung | verfügbar | Automatisch ausgeglichene Teams Alpha/Bravo, gemeinsame Platzierungspunkte, Teamrang und persistierte Teamzuordnung sind concurrency-getestet |
| Zuschauer | geplant | KW-027 bereitet die Anzeige vor; produktive Zuschauerrolle, Berechtigungen und Update-Priorisierung fehlen |
| XP/Level/Missionen | teilweise | Idempotentes Reward-Ledger, stabile Tages-/Wochenmissionen, Level-Fortschritt und 30+ Achievement-Definitionen vorhanden; vollständige Achievement-UI/E2E und alle Auditkriterien noch offen |
| Arena-Rating | teilweise | Transaktionale Persistenz mit RatingBefore/Delta/After für Arena-Ergebnisse vorhanden; Saisonmodell, Rivalen und vollständige Ranking-Abnahme offen |
| Profiltrends und Kalender | teilweise | SQL-aggregierte 7/30/90-Tage-Trends, Aktivitätskalender, Bestwerte und paginierte Historie vorhanden; Visual-/Accessibility-Abnahme offen |
| Datenschutz-Reset/Löschung | teilweise | Profil-Gate, Request-/Arena-/Completion-Drain, Sitzungs-Tombstone und Re-Provisioning sind concurrency- und HTTP-getestet; produktiver Zwei-Browser-Smoke offen |
| GHCR Multiarch Image | verfügbar | Release-Workflow veröffentlicht versionierte Multiarch-Images nach GHCR |
| Air-Gap Image-Archiv | verfügbar | Release-Workflow erzeugt amd64-Imagearchiv, Manifest und Prüfsummen |
| Playwright Visual Regression | geplant | KW-051 |
| SignalR-Lasttest | teilweise | In-process bis 100 Teilnehmende und echter Netzwerk-Smoke mit 64 Teilnehmenden, 64 Finishes, 0 Fehlern und bestätigter Persistenz; Soak-/Mehrraummatrix offen |
| Accessibility-Kernflüsse | teilweise | Axe, Tastatur, Dark/Light, Reduced Motion, Mobile und 200-%-Reflow für Login, Typing, Arena und Privacy automatisiert; NVDA und echtes Mobilgerät offen |
