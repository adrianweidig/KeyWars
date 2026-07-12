# KeyWars v0.3.0 – ausführbarer Implementierungsplan

<!-- markdownlint-disable MD024 -->

Stand: 11. Juli 2026
Ausgangsbasis: `v0.2.13` / `bd15322` plus lokaler Theme- und
Autoscroll-Arbeitsstand
Ziel: ein belastbarer `v0.3.0`-Releasekandidat ohne Feature-Expansion

## 1. Leitentscheidung und Cut Line

KeyWars bleibt ein modularer ASP.NET-Core-Monolith aus Razor Pages, Minimal
APIs, SignalR, EF Core und SQLite in einem Container. Für `v0.3.0` werden keine
Microservices, keine SPA, keine Serverdatenbank und keine Cloudtelemetrie
eingeführt.

Der Release schließt vier Kernabläufe:

1. Production-LDAPS-Anmeldung und JIT-Provisionierung;
2. serverautoritatives Tippen mit wiederholbarem Prepare/Begin/Finish-Vertrag;
3. eine Arena-Einzelrunde mit definierter Persistenzbestätigung, Reconnect- und
   Abbruchsemantik;
4. Export, Statistik-Reset und Profillöschung auch bei parallelen Sitzungen,
   aktiven Versuchen und wartender Arena-Persistenz.

Theme, Accessibility, Container, Restore und Release-Evidenz werden nur so weit
bearbeitet, wie diese vier Abläufe es für eine vertretbare Freigabe benötigen.
Neue Spielmodi, Multi-Round, Zuschauer, Saison/Rivalen, große
Frontend-Neustrukturierungen und allgemeine Architekturprogramme liegen
außerhalb der Cut Line.

Für `v0.3.0` gilt standardmäßig: keine Datenbankschemaänderung. Eine Ausnahme
ist nur zulässig, wenn ein belegtes Vertragsloch ohne Migration nicht korrekt
geschlossen werden kann und der Previous-Image-on-Upgraded-DB-Test grün ist.

Die aktuelle Theme-/Autoscroll-Arbeit wird fachlich getrennt integriert. Daraus
folgt nicht automatisch ein öffentliches `v0.2.14`. Ohne akuten Patchbedarf
geht sie direkt in `v0.3.0` ein.

## 2. Verifizierte Ausgangslage

### 2.1 Repository und Änderungssatz

- `master` und `origin/master` stehen auf `bd15322`, Tag `v0.2.13`.
- Geändert sind `_Layout.cshtml`, `site.css`, `site.js`, `typing.js`,
  `arena.js` und `tests/browser/arena.spec.js`.
- Neu sind `theme-init.js` und dieser Plan.
- Der getrackte Diff umfasst 481 Einfügungen und 77 Löschungen.
- `git diff --check` ist sauber.
- Der Arbeitsbaum ist absichtlich nicht sauber und darf vor T0 nicht
  pauschal überschrieben oder zusammencommittet werden.

### 2.2 Frische lokale Evidenz

| Prüfung | Ergebnis |
| --- | --- |
| .NET SDK / Runtime | SDK 10.0.301, Runtime 10.0.9 |
| Node / npm | Node 24.16.0, npm 11.13.0 |
| Release-Build | 0 Warnungen, 0 Fehler |
| Unit | 90/90 |
| Integration | 56/56 |
| Concurrency | 28/28 |
| HTTP/E2E | 11/11 |
| Chromium/Playwright | 20/20, ein Worker, 2,8 Minuten |
| Gesamtverhalten | 205/205; der Coverage-Lauf mit 185 .NET-Fällen wird nicht addiert |
| Format | `dotnet format --verify-no-changes` grün |
| Offline-Assets | `npm run assets:verify` grün |
| NuGet-Audit | keine aktuell gemeldeten anfälligen Pakete |
| npm-Audit | 0 bekannte Vulnerabilities gegen registry.npmjs.org |

Vereinte Line-Coverage der vier .NET-Collector-Berichte:

- 85,2 % einschließlich generierter Migrationen;
- 75,0 % ohne generierte Migrationen;
- `LdapAuthenticator.cs` und `ArenaHub.cs`: im Collector-Lauf 0 %;
- `ApiEndpoints.cs`: 27,1 %;
- mehrere PageModels: im Collector-Lauf 0 %.

Die 0-%-Werte beweisen nicht, dass kein externer Browser- oder Real-AD-Test
existiert. Sie beweisen, dass die aktuelle Coverage-Datei diese Evidenz nicht
zuordnet.

### 2.3 Last- und SignalR-Smokes

Der In-Memory-Smoke beendete Räume mit 2, 10, 25, 50 und 100 Teilnehmenden
vollständig. Progress-p95: 0,472 / 11,956 / 17,524 / 23,364 / 22,858 ms.

Ein frischer Netzwerk-Smoke mit dem offiziellen SignalR-.NET-Client gegen zwei
parallele Räume mit je zehn Teilnehmenden ergab:

- 20/20 beendete Teilnehmende und 20 Platzierungen;
- 0 Fehler;
- Command-p95 110,227 ms;
- Broadcast-p95 134,818 ms;
- Completion-Queue anschließend `pendingJobs=0` und `failedAttempts=0`.

Das ist ein lokaler funktionaler Smoke auf acht CPUs. Es ist weder ein SLO noch
ein Kapazitäts-, Soak-, Crash- oder Fault-Nachweis.

### 2.4 Bereits vorhandene, aber zu aktualisierende Evidenz

- `docs/implementation-status.md` dokumentiert einen früheren echten
  Production-LDAPS-E2E mit zwei temporären AD-Nutzern, deaktiviertem Konto,
  Reload/Reconnect und DB-Nachweis. Der Lauf wurde jetzt nicht wiederholt.
- Die Workflows erzeugen bereits GHCR-Multiarch, amd64-Air-Gap-Archiv,
  Prüfsummen, SBOM, Provenance und Release-Manifest. Diese Fähigkeiten werden
  nicht neu gebaut, sondern gegen den RC-SHA erneut gelesen und geprüft.
- Lokal steht kein Docker-Binary zur Verfügung. Container-, `/data`-,
  Offline- und Restore-Smokes müssen auf einem CI-/Remote-Runner erfolgen.

### 2.5 Visuelle Evidenzgrenze

Die 13 Kontaktbogenbilder sind Full-Page-Dateien mit tatsächlichen Maßen von
1265×750 bis 1265×1590 beziehungsweise 375×812. Ihre Erzeugung ist nicht
nachweisbar an den frischen Playwright-Lauf mit konfigurierten 1366×768 und
390×844 gebunden.

Der helle Screenshot mit Dateiname `login-dark` ist deshalb ein
Provenance-Konflikt, kein bewiesener Theme-Defekt. Künftig werden konfigurierter
Viewport, Content-Viewport, Bildmaß, Full-Page-Modus, Theme, URL, Seed, SHA und
Dateihash getrennt erfasst.

### 2.6 Synthesebewertung

| Bereich | Wertung | Begründung |
| --- | ---: | --- |
| Produktreife | 6,5/10 | Breite Pilotbasis, aber Kernrisiken in Timing, Completion, Restore und Accessibility |
| Architektur | 7,6/10 | Passender Monolith; Konzentration ist Wartungsrisiko, kein Umbauauftrag |
| Korrektheit | 7,5/10 | Starke Tests; früher partieller Sprintabschluss und Completion-Grenze sind offen |
| Security/Privacy | 7,0/10 | Solider Vertrag und frühere LDAPS-Evidenz; Cross-Session-/Queue-Rennen offen |
| Operabilität | 6,2/10 | Lieferkette und Health vorhanden; aktueller Container-/Restore-/Crash-Nachweis fehlt |
| Testreife | 8,3/10 | Fünf Testflächen plus Coverage und Netzwerksmoke; kritische Mapping-Lücken |
| Accessibility | 4,2/10 | Keine Axe-, Tastatur-, Zoom-, Kontrast- oder Screenreader-Freigabe |
| Desktop-UX | 7,2/10 | Kohärent, aber visuell dicht |
| Mobile-UX | 6,1/10 | Kernbedienung sichtbar, jedoch verschachteltes Scrollen und lange Flows |
| Visuelle Kohärenz | 6,9/10 | Dark stark; Light und Screenshot-Provenance noch unvollständig |

Es gibt keinen belegten P0. Releasekritische P1 sind:

1. ein partieller Zeitsprint kann im aktuellen Serverpfad vor Ablauf der
   Sollzeit als abgeschlossen gelten;
2. Arena-Finish und dauerhafte Completion sind nicht als ein beobachtbarer
   Commit-/Ack-Vertrag modelliert; Runtime-Retry und Queue-Full-Verhalten sind
   unvollständig;
3. eine wartende Completion kann mit Reset/Löschung konkurrieren und später
   erneut abgeleitete Daten schreiben;
4. Container-/Restore-/Rollback-Nachweis fehlt aktuell;
5. die vier Kernabläufe sind nicht barrierefrei freigegeben.

## 3. Unverhandelbare Invarianten

- `RewardLedgerEntry` ist die einzige XP-Autorität.
- `GamificationEvent` bleibt privater, abgeleiteter Präsentationsfeed.
- Live-Previews bleiben transient; vollständige Keystroke-Replays werden nie
  persistiert.
- Clientwerte bestimmen nie XP, Bestwert, Rating oder Abschlusszeit.
- Arena-Phase, Countdown, Startzeit und Sequenzakzeptanz bleiben servergeführt.
- Pro Idempotenzschlüssel entstehen höchstens eine Summary, eine Ratingwirkung
  und eine Ledgerwirkung.
- Ein Serverabbruch oder unbestätigter Abschluss erzeugt kein Rating und keine
  XP-Behauptung.
- Production erlaubt keinen Development-Login.
- Persistente Dateien liegen ausschließlich unter `/data`.
- Runtime bleibt ohne CDN und ohne Internetzugriff funktionsfähig.
- Bestehende Migrationen werden nicht manuell umgeschrieben.
- Eine bestätigte Profillöschung darf durch spätere Queue-Verarbeitung keine
  personenbezogenen oder abgeleiteten Daten wiederbeleben.

## 4. Präziser Releasevertrag

### 4.1 Identity

- Statisch ungültige Production-Konfiguration blockiert den Start.
- Ein transient nicht erreichbares LDAP blockiert nicht automatisch den
  Prozessstart; der Login schlägt generisch fehl und die definierte
  Betriebsanzeige meldet die Directory-Störung ohne Identität.
- LDAPS beziehungsweise explizit erlaubtes StartTLS validiert CA und Hostname.
- JIT bindet das Profil stabil an die Directory-GUID.
- Ein gelöschtes Profil und jede dazugehörige alte Cookie-Sitzung verlieren
  Zugriff; ein späterer Directory-Login erzeugt ein neues Profil.

### 4.2 Typing

- Modus bestimmt Dauer und Regeln; `SprintSeconds` vom Client ist keine
  Autorität.
- Begin setzt `StartedAt` und für Sprints `EndsAt` serverseitig.
- Ein vollständiger fehlerfreier Zieltext darf vor Ablauf beendet werden.
- Ein partieller Sprint darf erst ab `EndsAt` abgeschlossen werden.
- Ein zu früher partieller Finish liefert einen typisierten Konflikt mit
  verbleibender Serverzeit und verändert weder Attempt noch Ledger.
- Retry nach verlorener Antwort liefert dasselbe persistierte Resultat.
- Zwei gleichzeitige Finish-Requests erzeugen maximal einen Abschluss und eine
  Ledgerwirkung; der zweite erhält das kanonische Resultat statt eines 500ers.
- Ein Prozessneustart beendet persistierte Prepared/Started-Attempts neutral;
  er rekonstruiert keine Eingabe.

### 4.3 Arena

Die UI unterscheidet:

1. `Running` – Rennen läuft;
2. `FinishedPending` – Spielresultat steht im Speicher, Persistenz offen;
3. `Persisted` – Summary, Rating und Rewards sind transaktional committed;
4. `Failed` – Persistenz fehlgeschlagen, keine Erfolgsbehauptung;
5. `AbortedUnconfirmed` – Prozess/Server verlor den unbestätigten Abschluss.

Podium, WPM und Platzierung dürfen als vorläufiges Spielresultat erscheinen.
Rating, XP und „gespeichert“ erscheinen erst bei `Persisted`.

### 4.4 Privacy

Der bestehende Vertrag bleibt:

- Reset löscht Attempts, AttemptErrors, ChallengeAttemptBindings,
  RewardLedger, Missions, Achievements, GamificationEvents und
  WeaknessObservations; er setzt XP, Level, Streak, Season, Rating und
  RatedMatchCount auf Startwerte.
- Identität, eigene Texte/Sammlungen und historische Challenge-/Arena-Ergebnisse
  bleiben beim Reset.
- Delete entfernt dieselben abgeleiteten Daten, entfernt Sammlungen,
  leert/pseudonymisiert eigene Texte, lehnt aktive Challenge-Teilnahmen ab,
  pseudonymisiert das Profil und behält historische Gruppen-/Arena-Referenzen.
- Delete signiert die aktuelle Sitzung aus; alle anderen Sitzungen werden beim
  nächsten Request sauber verworfen.

### 4.5 Betrieb und Rollback

- `v0.3.0` wird ohne Schemaänderung geschnitten, sofern kein belegter Blocker
  dies verhindert.
- Restore wird offline und gestaged durchgeführt; ein fehlgeschlagener Restore
  ersetzt nie die letzte intakte Datenbank.
- Rollback verwendet vorheriges Image plus vor dem Rollout geprüftes Backup.
- Laufende Arenen werden bei Rollback neutral abgebrochen.

## 5. Abhängigkeitsgraph

```text
T0 Evidenz- und Scope-Freeze
├── T1 Theme/Autoscroll
│   └── T2 Typing-Vertrag
│       └── T3 Arena-Completion/Faults
│           └── T4 Privacy-Rennen
│               └── T5 Accessibility
│                   └── RC
├── C1 Critical Coverage Mapping ───────────────┘
├── O1 Container-/Security-Vertrag
│   └── O2 Backup/Restore/Rollback ─────────────┘
└── I1 Auth-/TLS-Matrix
    └── I2 Real-AD-Refresh nach Freeze ─────────┘
```

T1, O1 und I1 können nach T0 unabhängig beginnen. T2 bis T5 bleiben
absichtlich seriell, weil sie denselben Browser-, API-, Queue- und
Privacy-Vertrag verändern.

## 6. Arbeitspakete

## T0 – Evidenz-, Scope- und Versions-Freeze

### Ziel

Einen reproduzierbaren Ausgangspunkt herstellen, bevor Verhalten geändert
wird.

### Betroffene Dateien

- `docs/implementation-status.md`
- `docs/features.md`
- `docs/test-strategy.md`
- `scripts/check_implementation_status.py`
- Screenshot-/Audit-Helper unter `tests/browser` beziehungsweise `scripts`

### Umsetzung

1. Theme und Autoscroll als getrennte Changes/Commits vorbereiten.
2. Test- und Screenshotartefakte an SHA, Datum, Umgebung, Befehl und Hash
   binden.
3. Bei Screenshots zusätzlich erfassen:
   - konfigurierte Viewportgröße;
   - `window.innerWidth/innerHeight`;
   - resultierendes Bildmaß;
   - Full-Page-Flag;
   - `html[data-theme]` und `color-scheme`;
   - Route, Seed, Nutzerrolle und Dateihash.
4. Bestehende Status-/Feature-Tabellen bleiben kanonisch.
5. Stabile Acceptance-IDs, Evidenztyp, Testreferenz, SHA und Prüfdatum als
   strukturierte Spalten ergänzen.
6. Validator schlägt bei widersprüchlichen Aussagen, fehlenden Dateien,
   ungültigen Testreferenzen oder `erledigt/verfügbar` ohne Evidenz fehl.
7. Unterstützte Browser festschreiben. Empfehlung für `v0.3.0`:
   Chromium-Familie Chrome/Edge im aktuellen Enterprise-Kanal; Firefox/Safari
   nur nach expliziter Scope-Erweiterung.
8. Pilotparameter vor Coding-Freeze dokumentieren. Empfehlung, falls der
   Betreiber nichts anderes vorgibt: 10–25 reale Pilotnutzer, sieben
   Arbeitstage, höchstens zwei parallele Räume; synthetischer 64-Client-Lauf
   separat.
9. Entscheidung dokumentieren: kein öffentliches `v0.2.14` ohne akuten
   Patchbedarf.

### Tests

- Validator-Self-Tests mit absichtlich fehlender, widersprüchlicher und
  veralteter Evidenz.
- `git diff --check`.
- kompletter Build und bestehende 205 Fälle gegen den fixierten SHA.

### Gate

- sauber identifizierter Baseline-SHA;
- keine unbelegte Statusbehauptung;
- Screenshotkonflikte sind sichtbar, nicht still normalisiert;
- Theme und Autoscroll einzeln revertierbar.

### Rollback

Nur Dokumentations-/Validator-Revert; keine Runtime- oder Datenänderung.

## T1 – Theme und Graphem-Autoscroll releasefähig machen

### Voraussetzungen

T0 abgeschlossen.

### Betroffene Dateien

- `Pages/Shared/_Layout.cshtml`
- `wwwroot/js/theme-init.js`
- `wwwroot/js/site.js`
- `wwwroot/js/typing.js`
- `wwwroot/js/arena.js`
- `wwwroot/css/site.css`
- `tests/browser/arena.spec.js`; bei Bedarf fachlich schmale neue Spec

### Umsetzung Theme

1. Priorität festschreiben:
   gültige gespeicherte Wahl → Systempräferenz → Dark als dokumentierter
   Fallback.
2. Bootstrap vor dem Stylesheet behalten; er darf weder inline CSP-Ausnahmen
   noch externe Assets benötigen.
3. Login und authentifizierte Shell verwenden denselben aufgelösten
   Theme-Vertrag.
4. Desktop- und Mobile-Schalter spiegeln gleichzeitig:
   `data-theme`, `aria-pressed`, Titel, zugänglichen Namen und Icon.
5. Ungültiger Storage-Wert, blockiertes `localStorage` und fehlendes
   `matchMedia` fallen deterministisch zurück.
6. Systemtheme-Änderung wirkt nur, solange keine gespeicherte Nutzerwahl
   existiert.
7. Light-Tokens für Text, Muted, Gold/XP, Focus, Formfelder und Quest-Karten
   messen und nur belegte Kontrastfehler korrigieren.

### Umsetzung Autoscroll

1. Zielwechsel setzt nur die innere Zieltextposition zurück.
2. Das aktuelle Graphem bleibt innerhalb eines oberen/unteren Sichtmargins.
3. Korrektur im nächsten Animation Frame verändert weder Cursor noch
   Eingabefokus noch Dokumentscrollposition.
4. Training, eigener Arena-Text und fremde Live-Preview erfüllen denselben
   Geometrievertrag.
5. NFC/NFD, kombinierende Zeichen, Emoji/ZWJ, CRLF/LF und Absätze werden
   getrennt getestet.
6. Auf Mobile werden verschachtelte Scrollflächen reduziert, falls der
   Bildschirmtastaturtest Konflikte zeigt.
7. Die derzeit doppelte Scrollfunktion wird nur dann in einen kleinen
   gemeinsamen Helper extrahiert, wenn T1 vollständig grün ist; keine weitere
   JavaScript-Neustrukturierung.

### Tests

- Cold Start, Reload, Navigation, Systemtheme-Wechsel und Storage-Fehler.
- 320×568, 390×844, 768×1024 und 1366×768.
- Dark/Light für Login, Dashboard, Tippen und Arena.
- Fokus, Cursor, Dokument-`scrollY` und inneres `scrollTop` vor/nach Eingabe.
- 200-%-Zoom und Reduced Motion.
- Browserkonsole und Page Errors als Artefakt.

### Gate

- kein falscher Theme-Frame im definierten Capture-Verfahren;
- Controls zeigen identischen Zustand;
- aktuelles Graphem sichtbar;
- Fokus und Cursor stabil;
- kein Inhaltsverlust oder horizontaler Dokumentoverflow;
- geänderte Komponenten erfüllen WCAG-Kontrastwerte.

### Rollback

Theme und Autoscroll getrennt revertieren; keine Migration.

## C1 – Critical Coverage Mapping

### Ziel

Kritische Verträge einer konkreten Evidenz zuordnen, ohne eine beliebige globale
Coveragequote zum Qualitätsziel zu machen.

### Umsetzung

1. Mapping für `LdapAuthenticator`, `ArenaHub`, `ApiEndpoints`,
   Login-/Privacy-PageModels, `AttemptService` und Completion Queue erstellen.
2. Evidenztypen unterscheiden:
   - instrumentierter Unit-/Integrationstest;
   - externer Browser-E2E;
   - Real-AD-E2E;
   - manueller Betriebssmoke;
   - aktuell nicht abgedeckt.
3. `ApiEndpoints` gezielt auf Auth, Origin/Content-Type, Validierung,
   Idempotenz und typisierte Fehlerantworten testen.
4. Für `ArenaHub` einen in-process SignalR-Test mit offiziellem Client
   bevorzugen. Falls der Prozess technisch extern bleiben muss, wird die
   Artefaktreferenz explizit statt einer falschen 0-%-Interpretation geführt.
5. Für LDAP reine Normalisierungs-, Filterescape-, TLS-/Hostname- und
   Fehlerklassifikationslogik in schmale testbare Komponenten extrahieren;
   keine Auth-Neuschreibung.
6. Generierte Migrationen aus der entscheidungsrelevanten Coverage ausnehmen.

### Gate

Jeder releasekritische Vertrag besitzt mindestens eine aktuelle
Evidenzreferenz. Ein 0-%-Collector-Wert bleibt nur mit expliziter externer
E2E-Zuordnung bestehen.

## T2 – Kanonischen Typing- und Request-Lifecycle schließen

### Voraussetzungen

T1 grün; C1-Mapping für Typing/API vorhanden.

### Betroffene Dateien

- `Domain/TypingEngine.cs`
- `Domain/Competition.cs`
- `Services/AttemptService.cs`
- `Services/AttemptSessionStore.cs`
- `Services/AttemptModels.cs`
- `Infrastructure/ApiEndpoints.cs`
- `wwwroot/js/typing.js`
- Unit-, Integration-, E2E- und Browsertests

### Umsetzung

1. Kanonischen Messvertrag in `docs/typing-metrics.md` festschreiben:
   Normalisierung, Grapheme, Alignment, Resttext, Zeit, Vollständigkeit,
   Accuracy, WPM, Konsistenz und Fehlerarten.
2. Begin-Response um serverseitiges `EndsAt` für Sprintmodi erweitern.
3. Server leitet Sprintdauer ausschließlich aus `TrainingMode` ab.
4. Finish-Regel implementieren:
   - vollständiger fehlerfreier Text: vorzeitig zulässig;
   - partieller Text vor `EndsAt`: keine Mutation, HTTP 409,
     stabiler Fehlercode `attempt_still_running` und `retryAfterMs`;
   - partieller Text ab `EndsAt`: zulässig;
   - verspäteter Request: Dauer auf Moduslimit begrenzen.
5. InvalidOperation-Ausnahmen nicht als generische 500er ausliefern.
   Ein schmaler API-Fehlermapper liefert ProblemDetails mit stabilen Codes für
   abgelaufen, falscher Nonce, nicht begonnen, noch laufend, zu lang und
   bereits beendet.
6. Gleichzeitige Finish-Requests serialisieren:
   - per Attempt-ID atomare Session-Transition oder Lock;
   - Persistenz und Motivation in einer DB-Transaktion;
   - zweiter Request liest das bereits persistierte kanonische Ergebnis.
7. `AttemptSessionStore.RemoveProfile` ergänzen.
8. Beim Prozessstart oder beim ersten Zugriff persistierte
   Prepared/Started-Attempts ohne In-Memory-Session als neutral
   Aborted/Expired markieren; niemals rekonstruieren.
9. Client behandelt 409 mit Server-Restzeit, verlorene Antwort und Retry ohne
   doppelte Ergebnisdarstellung.
10. `WeaknessObservation`-Neugewichtung und Rebuild bleiben außerhalb
    `v0.3.0`, sofern keine Kerninvariante sie verlangt.

### Tests

- Property-/Golden-Tests für NFC/NFD, Combining, Emoji/ZWJ, CRLF/LF,
  Insert/Delete/Substitution und ungeschriebenen Rest.
- Integration:
  - früher partieller Sprint abgelehnt;
  - früher vollständiger Sprint akzeptiert;
  - exakt am/nach Deadline akzeptiert;
  - falsche/fehlende Nonce;
  - zwei parallele Finishes;
  - verlorene Antwort und Retry;
  - Prozessrestart mit Prepared/Started-Attempt;
  - maximal ein Ledger-Source-Key.
- Browser:
  Prepare → Begin → echte Eingabe → Finish; Reload vor/nach Begin; simulierte
  verlorene Finish-Antwort; verständliche Fehler-UX.

### Observability

Niedrig-kardinale Zähler für prepared, begun, accepted, duplicate, rejected,
expired und aborted. Keine Nutzernamen, Nonces oder Zieltexte.

### Gate

- kein partieller Sprint vor Deadline im Leaderboard;
- Domain, DB und UI zeigen dieselben Werte;
- maximal ein Attemptabschluss und eine Ledgerwirkung;
- Retry liefert dasselbe Resultat;
- Restart erzeugt keine verwaisten aktiven Attempts.

### Rollback

Code-Revert; API-Erweiterung abwärtskompatibel; keine Migration.

## T3 – Arena-Commit, Retry, Reconnect und Faults

### Voraussetzungen

T2 grün; beworbene Maximalgröße bleibt 64; Pilotprofil aus T0.

### Betroffene Dateien

- `Services/LiveRoomManager.cs`
- `Services/LiveRoomCompletionQueue.cs`
- `Services/LiveProgressBroadcaster.cs`
- `Hubs/ArenaHub.cs`
- `Infrastructure/ApiEndpoints.cs`
- `wwwroot/js/arena.js`
- Live-/Concurrency-/Integration-/Browser-/Loadtests

### Umsetzung Completion

1. `CompletionJobState` mit `Pending`, `Persisted` und `Failed` in der Queue
   führen; Schlüssel ist Raum-ID plus Idempotenzschlüssel.
2. `Enqueue` liefert eine Receipt statt nur `void`.
3. Queue-Kapazität beim Start gegen `MaxConcurrentRooms` validieren. Bei einer
   Einzelrunde muss für jeden gleichzeitig erlaubten Raum ein Abschlussplatz
   reservierbar sein.
4. Queue-Full darf keinen Raum mit `PersistenceQueued=true` ohne Job
   hinterlassen. Enqueue und Room-State werden als expliziter
   Erfolg/Fehlerübergang behandelt.
5. Transiente SQLite-Fehler werden auch im laufenden Betrieb mit begrenztem
   Backoff erneut versucht, nicht erst bei Flush/Shutdown.
6. Permanente Fehler bleiben als `Failed` sichtbar; sie werden nicht als
   gespeichert dargestellt und wachsen nicht unbemerkt.
7. Authentifizierten Endpoint
   `GET /api/arena/{roomId}/speicherstatus` ergänzen:
   - zuerst persistierte Summary lesen;
   - sonst Queue-State;
   - nach Restart ohne Summary `aborted_unconfirmed`.
8. `arena.js` zeigt Pending/Persisted/Failed semantisch und pollt begrenzt.
   Rating/XP werden erst nach DB-Commit behauptet.
9. Failed-/Pending-Status besitzt eine barrierefreie Live-Region.

### Umsetzung Privacy-Kopplung

1. Queue führt pro Completion die betroffenen Profile.
2. `DrainProfileAsync(profileId)` wartet auf deren laufende Jobs und liefert
   Erfolg oder expliziten Fehler.
3. Ein Delete darf erst nach erfolgreichem profilbezogenem Drain in die
   Pseudonymisierungstransaktion gehen.
4. Ein ungelöster permanenter Completion-Fehler blockiert Delete mit
   verständlicher Retry-UX, statt nachträglich Daten wiederzubeleben.

### Umsetzung Reconnect/Crash

1. Verbindungszustände `connected/reconnecting/disconnected` anzeigen.
2. Alte oder doppelte Sequenzen bleiben serverseitig wirkungslos.
3. Nach hartem Prozessabbruch:
   - Raum ist nicht wiederaufnehmbar;
   - Client zeigt neutralen Abbruch;
   - ohne Summary kein Rating/XP;
   - keine Rekonstruktion aus Clientdaten.
4. `LiveRoomManager` bleibt Fassade. Es werden nur Seams für Clock,
   Completionstatus und Fault-Injection extrahiert, keine vollständige
   Zerlegung.

### Tests

- Bestehende 28 Concurrency-Fälle unverändert grün.
- Queue-Full bei absichtlich kleiner Kapazität.
- Transienter Lock, permanenter Writerfehler und Runtime-Retry.
- Prozessabbruch:
  - Lobby;
  - Countdown;
  - Running;
  - nach In-Memory-Finish vor Enqueue;
  - nach Enqueue vor Commit;
  - nach Commit vor Clientabfrage.
- Offline/Online, Reload, Reconnect-Sturm, alte/doppelte/vertauschte Sequenzen.
- Ein echter 64-Client-Raum als funktionales Gate.
- Parallelräume entsprechend Pilotprofil.
- geplanter 30-Minuten-Nominal- und 60-Minuten-Release-Soak auf festem Runner.
  Performancebudgets werden nach erster Baseline derselben Hardware gesetzt;
  Invarianten gelten sofort.

### Observability

- aktive Räume/Verbindungen;
- Queue Pending/Failed/Capacity;
- Persistenzdauer und Retryzahl;
- Completionstatus und Abbruchgrund;
- Shutdown-Restbestand;
- Command-/Broadcast-/Completion-p50/p95/p99;
- keine Raumcodes, Namen, GUIDs oder Zieltexte als Dimension.

### Gate

- höchstens eine Summary, Rating- und Ledgerwirkung je Schlüssel;
- kein Rating/XP bei `Failed` oder `AbortedUnconfirmed`;
- kein Client-„gespeichert“ vor Commit;
- Queue und Speicher kehren nach Abschluss auf Baseline zurück;
- permanenter Fehler ist sichtbar und löst Stopkriterium aus;
- 64-Client-Funktionslauf und definierte Soaks ohne Invariantenbruch.

### Rollback

Vorheriges Image; laufende Räume neutral abbrechen; keine Datenmigration; keine
Rekonstruktion aus Clientdaten.

## T4 – Privacy-End-to-End und Sitzungswiderruf

### Voraussetzungen

T2 und T3 einschließlich `DrainProfileAsync` grün.

### Betroffene Dateien

- `Services/ProfilePrivacyService.cs`
- neuer kleiner Singleton `ProfileAccessGate`
- `Auth/CurrentUser.cs` und Cookie-Validierung in `Program.cs`
- `Services/AttemptSessionStore.cs`
- Privacy-Pages und Tests

### Umsetzung

1. Vor Reset/Delete einen profilbezogenen Access Gate setzen. Die
   Single-Instance-Invariante macht den In-Memory-Gate ausreichend.
2. Neue API-/Page-/Hub-Aktionen dieses Profils werden während der Operation
   abgewiesen.
3. Aktive Attempts aus `AttemptSessionStore` entfernen und persistierte
   nonterminale Attempts neutral abbrechen.
4. Profil aus allen Live-Räumen entfernen.
5. Profilbezogene Arena-Completions drainen.
6. Erst danach bestehende Reset-/Delete-DB-Transaktion ausführen.
7. Bei einem Fehler vor der DB-Transaktion Gate sauber freigeben; bei einem
   unklaren Completionstatus keine Löschung behaupten.
8. Cookie `OnValidatePrincipal` prüft Gate und `!Deleted`. Ungültige alte
   Sitzungen werden rejected und zum Login geführt, nicht auf eine Fehlerseite.
9. Nach erfolgreichem Delete Gate als Tombstone bis Prozessende behalten;
   nach Restart schützt `Deleted` in der DB.
10. Exportinventar automatisiert gegen alle profilbezogenen DbSets prüfen.
11. Historische Challenge-/Arena-Ergebnisse bleiben referenziell erhalten und
    zeigen ausschließlich das pseudonymisierte Profil.

### Tests

- zwei Browserkontexte plus zwei Tabs;
- laufender Typing-Attempt während Reset/Delete;
- aktive Arena in Lobby und Running;
- Completion pending, persisted und permanent failed;
- Request aus alter Sitzung nach Delete;
- Export-Isolation;
- exakte Tabellen-Postconditions für Reset und Delete;
- Re-Provisionierung derselben Directory-GUID erzeugt neues Profil;
- historische Ergebnisse bleiben ohne alte Identität;
- keine Ledger-/Gamification-Wiederbelebung nach Delete.

### Observability

Nur Ereignistyp, Ergebnis, Dauer und Fehlerklasse. Keine Bestätigungsnamen,
Directory-GUIDs oder exportierten Inhalte.

### Gate

- bestehender Vertrag exakt erfüllt;
- alle Sitzungen verlieren Zugriff;
- keine aktive Arena-/Attempt-Präsenz;
- keine spätere Completion schreibt gelöschte Derived-Daten;
- Re-Provisionierung sauber.

### Rollback

Code-Revert möglich. Eine bestätigte Löschung selbst bleibt absichtlich
irreversibel und wird durch Systemrollback nicht sichtbar gemacht.

## T5 – Accessibility und Mobile-Freigabe der Kernabläufe

### Voraussetzungen

T1 bis T4 funktional stabil.

### Umsetzung

1. `@axe-core/playwright` als lokale DevDependency; keine Runtime-Abhängigkeit.
2. Matrix: Login, Typing vorbereitet/laufend/Resultat, Arena
   Lobby/Countdown/Running/Pending/Persisted/Failed und Privacy-Bestätigung.
3. Dark/Light, Desktop/Mobile, Reduced Motion und 200-%-Zoom.
4. Tastatur:
   Skip-Link, Navigation, Theme, Formulare, Start/Finish, Ready/Start/Give-up,
   Fehlerzustände und Löschbestätigung.
5. Fokusmanagement nach Reload, Reconnect, 409, Completionstatus und Signout.
6. Live-Regionen dürfen keine Zeichenflut aus transienten Previews vorlesen.
7. Touchziele mindestens 44×44 CSS-Pixel.
8. Mobile Bildschirmtastatur auf einem realen Gerät prüfen; Desktop-Emulation
   allein gilt nicht als Nachweis.
9. Manueller NVDA-Smoke unter Windows mit Chrome/Edge für Namen, Rollen,
   Zustände, Fehler und Live-Regionen.

### Gate

- keine kritischen/schweren Axe-Befunde;
- Kernabläufe ohne Maus;
- Text 4,5:1, große Schrift und UI-Komponenten/Fokus 3:1;
- kein Inhaltsverlust bei 200 % und 320×568;
- aktuelles Graphem, Eingabe und Status bleiben mit Bildschirmtastatur
  erreichbar;
- datierter Screenreader-Nachweis.

### Rollback

Semantik-, CSS- und JS-Korrekturen in kleinen Changes; keine pauschale
Rücknahme zusammen mit einem Redesign.

## I1 – Auth-, TLS-, Proxy- und Header-Matrix

### Voraussetzungen

T0 und C1.

### Umsetzung

1. Statische Fehlerklassen testen:
   fehlende URLs/BaseDN/UPN, unsicheres LDAP, fehlende CA, ungültige Timeouts,
   Development-Login in Production.
2. Transiente Fehlerklassen testen:
   DNS/Timeout/Directory nicht erreichbar, falsche Credentials, deaktivierter
   Nutzer.
3. Filterescape, Bindname, Searchname, CA-Chain und Hostnameprüfung in
   testbare schmale Komponenten überführen.
4. HTTPS-/Forwarded-Proto-Vertrag hinter ausschließlich bekannten
   Proxyadressen testen.
5. Cookies: Secure, HttpOnly, SameSite, Host-Prefix und Lebensdauer.
6. CSP, Frame, MIME, Referrer und Permissions Policy prüfen.
7. HSTS-Verantwortung explizit festlegen. Empfehlung: App emittiert HSTS nach
   vertrauenswürdigem Forwarded-Proto; Proxy darf zusätzlich härten.
8. Health und Logs enthalten keine Identitäten, Filter oder Zieltexte.

### Gate

Fail-closed bei statischer Unsicherheit; generische Loginfehler; kein
Development-Login; korrekte Header über HTTPS/Proxy; keine PII in
Health/Logs.

## I2 – Echter LDAPS-Refresh gegen RC-SHA

### Voraussetzungen

Feature-Freeze; I1 grün; finale Containerkonfiguration.

### Test

- gültiger Nutzer und JIT;
- zweiter Nutzer;
- falsches Passwort;
- deaktivierter Nutzer;
- CA-/Hostname-Vertrag;
- Reload und SignalR-Reconnect;
- DB-Evidenz für zwei Directory-GUIDs;
- Testnutzer anschließend entfernen;
- SHA, Datum, Umgebung und Artefakte dokumentieren.

### Gate

Der frühere Nachweis ist durch einen aktuellen Lauf gegen exakt den RC-SHA
ersetzt.

## O1 – Container-, `/data`-, Keyring- und Offline-Vertrag

### Voraussetzungen

Dockerfähiger CI-/Remote-Runner.

### Umsetzung/Test

1. Image aus dem fixierten SHA bauen.
2. Production ohne LDAP-Konfiguration muss fail-closed enden.
3. Development-Smoke mit `--network none` beweist Offline-Assets und lokale
   Kernseiten.
4. Container läuft als nichtprivilegierter Nutzer, `read_only`, `cap_drop
   ALL` und `no-new-privileges`.
5. Schreibzugriffe sind nur `/data` und explizite `tmpfs`-Pfade.
6. Datenbank und Data-Protection-Keyring über Containerneustart erhalten.
7. Keyring-Vertrag dokumentieren:
   - v0.3 garantiert Persistenz und restriktive Dateirechte;
   - Verschlüsselung at rest ist nur garantiert, wenn der Betreiber ein
     explizit unterstütztes Volume-/Zertifikatsmodell bereitstellt;
   - die Development-Warnung wird nicht als Production-Nachweis verwendet.
8. Compose-Konfiguration und Healthchecks aus dem Releaseartefakt lesen.
9. Alle Runtime-Requests auf externe Hosts blockieren und protokollieren.

### Gate

Offline startbar, nicht privilegiert, persistente Daten/Keys, keine
unerlaubten Schreib- oder Netzwerkpfade.

## O2 – Backup, atomarer Restore und Rollback-Readback

### Voraussetzungen

O1 grün; repräsentativer Testdatenbestand.

### Umsetzung

1. Backupmanifest mit SHA256, Appversion, Schema/Migrationsstand, Größe und
   Erstellungszeit erzeugen.
2. `PRAGMA integrity_check` auf dem Backup ausführen.
3. Restore nur im Maintenance-Prozess ohne laufenden Webhost.
4. Backup in `keywars.db.restore-<guid>` auf demselben Dateisystem einspielen.
5. Staging-DB erneut auf Integrität und erwarteten Migrationsstand prüfen.
6. Bestehende DB vor Austausch als Pre-Restore-Backup sichern.
7. Nach Schließen aller Verbindungen Staging per atomarem Same-Volume-Rename
   aktivieren; veraltete WAL/SHM-Dateien kontrolliert behandeln.
8. Bei Fehler bleibt die bisherige DB unverändert.
9. Frische Instanz mit restauriertem `/data` starten.
10. Nicht nur Counts, sondern Invarianten für Profile, Attempts, Ledger,
    Challenges, Arena, Privacy und Idempotenz vergleichen.
11. Beschädigtes Backup, falscher Pfad, Semikolonpfad, voller/nicht
    beschreibbarer Datenträger und abgebrochener Restore.
12. Wenn `v0.3.0` doch eine Migration enthält: vorheriges Image gegen
    aktualisierte DB lesen; andernfalls Release blockieren oder Backup-Restore
    als expliziten Downgradepfad testen.

### Gate

Backup-to-empty-restore grün; bisherige DB überlebt jeden injizierten
Restorefehler; RPO/RTO gemessen und dokumentiert; Previous-Image-Readback bei
jeder Schemaänderung.

## RC – Release Candidate, Pilot und Rollout

### Voraussetzungen

T1–T5, C1, O1–O2 und I1–I2 grün.

### Releaseartefakte

- versioniertes GHCR-Image;
- bestehendes Multiarch- und amd64-Air-Gap-Artefakt;
- Compose/env;
- SHA256SUMS;
- SBOM/Provenance;
- Release-Manifest;
- Test-, Accessibility-, Last-, Restore- und AD-Evidenz an denselben SHA.

### Pilot

Vor Start:

1. Betreiber, Abbruchverantwortlicher, Pilotgröße und Dauer benennen.
2. Zielinstanz sichern und Restore beweisen.
3. vorheriges Image lokal verfügbar halten.
4. laufende Arenen vor Upgrade kontrolliert neutral beenden.

Im Pilot muss mindestens einmal erfolgen:

- gültiger und ungültiger Login;
- vollständiger und partieller Sprint;
- Retry nach verlorener Antwort;
- Arena normal, Reconnect und neutraler Abbruch;
- Export, Reset und Delete in Testprofilen;
- Containerneustart mit Daten-/Keyring-Readback.

Sofortige Stopkriterien:

- doppelte Ledger-/Ratingwirkung;
- Rating oder XP nach Serverabbruch/Failed Completion;
- `failedAttempts>0` ohne erklärten und behobenen Testfehler;
- Queue bleibt über den definierten Drainzeitraum pending;
- Delete lässt Zugriff bestehen oder Derived-Daten wiederaufleben;
- Restore-/Readback-Fehler;
- ungeklärte Exception in einem Kernablauf;
- kritischer/schwerer Accessibility-Befund.

### Rollout

1. Feature-Freeze und finaler RC-SHA.
2. Backup plus Restore-Probe.
3. Pilot.
4. Evidenzreview und explizite Freigabe.
5. breitere Auslieferung.
6. 24/48-Stunden-Readback von Auth, Queue, Rating, Ledger, Restore und Errors.

### Rollback

- neue Starts sperren;
- laufende Arenen neutral abbrechen;
- vorheriges Image aktivieren;
- wegen der No-Schema-Cut-Line dieselbe DB weiterverwenden;
- falls doch migriert: ausschließlich den vorab bewiesenen
  Previous-Image-/Restorepfad verwenden;
- Privacy-Löschungen niemals rückgängig sichtbar machen.

## 7. Durchgehende Testbefehle

Lokale Basis:

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet format .\KeyWars.slnx --verify-no-changes --no-restore
dotnet build .\KeyWars.slnx -c Release --no-restore
dotnet test .\KeyWars.slnx -c Release --no-build --no-restore
npm run assets:verify
npm run test:browser
```

Coverage:

```powershell
dotnet test .\KeyWars.slnx -c Release --no-build --no-restore `
  --collect:"XPlat Code Coverage" `
  --results-directory .\output\coverage
```

SignalR:

```powershell
dotnet run --project .\tools\KeyWars.LoadTest -c Release --no-build -- `
  --signalr `
  --base-url http://127.0.0.1:5191 `
  --participants 64 `
  --rooms 1 `
  --steps 12 `
  --json .\output\signalr-64.json
```

Zusätzlich nach Paket:

- T1/T5: Axe, Tastatur, Zoom, Screenreader und reales Mobile;
- T2: Retry-/Parallel-/Restart-Matrix;
- T3: Fault-, Crash-, 64-Client- und Soak-Matrix;
- T4: Multi-Session-/Arena-/Completion-Privacy;
- I2: `npm run test:real-ad` gegen RC-SHA;
- O1/O2: Container-, Offline-, Restore- und Previous-Image-Readback.

Neue kritische Browserfälle laufen vor Merge zehnmal. Das ist ein
Flakiness-Signal, keine mathematische Garantie.

## 8. Explizit nach `v0.3.0` verschoben

- vollständige Aufteilung von `site.css`;
- allgemeine JavaScript-Modularisierung;
- vollständige Zerlegung von `LiveRoomManager`;
- allgemeines Browser-Spec-Redesign;
- produktweite Pixelbaseline;
- Weakness-Rebuild und neue Analytics;
- vollständige Nebenflächenabnahme für Texte, Rewards, Achievements,
  Rankings und Challenges, sofern kein P1 entsteht;
- Multi-Round, Best-of, Revanche und Zuschauer;
- Saison- und Rivalenmodell;
- Playwright-/MessagePack-/Dependency-Majorsprünge ohne belegten Bedarf;
- horizontale Skalierung, mehrere Instanzen, externe DB;
- persistente Wiederaufnahme laufender Rennen;
- neue Cloud-, CDN- oder Runtime-Dienste.

## 9. Definition of Done je Arbeitspaket

Ein Paket ist nur fertig, wenn:

- Vertrag, Fehlerfälle und Nicht-Ziele dokumentiert sind;
- niedrigste sinnvolle Testschicht Happy Path und Risiko abdeckt;
- SHA, Datum, Umgebung, Befehl und Artefakt referenziert sind;
- Security-, Privacy-, Accessibility- und Offline-Auswirkung geprüft sind;
- Logs/Metriken keine PII oder hochkardinalen Dimensionen enthalten;
- API-Fehler typisiert und nutzerverständlich sind;
- Datenänderung Migration, Restore, Readback und Rollback abdeckt;
- Browseränderung Dark/Light, Desktop/Mobile, Tastatur, Zoom und Reduced Motion
  berücksichtigt;
- Gate grün und Rollback praktisch ausführbar ist;
- Change einzeln review- und revertierbar bleibt.

## 10. Exakte Ausführungsreihenfolge

1. T0 Scope/Evidenz.
2. Parallel: T1, I1, O1 und Beginn C1.
3. T2 Typing.
4. T3 Arena.
5. T4 Privacy.
6. T5 Accessibility.
7. O2 Restore nach verfügbarem Container.
8. C1 schließen.
9. Feature-Freeze.
10. I2 Real-AD-Refresh.
11. RC-Artefakte und Readback.
12. Pilot.
13. Freigabe oder Rollback.

Der fachlich längste Pfad ist T0 → T1 → T2 → T3 → T4 → T5 → RC. Der
operative Pfad O1 → O2 und der Identity-Pfad I1 → I2 müssen vor demselben RC
abgeschlossen sein.
