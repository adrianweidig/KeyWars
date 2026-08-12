# Last- und Kapazitätstests

`tools/KeyWars.LoadTest` prüft zwei verschiedene Dinge:

- ohne `--signalr`: schneller In-Memory-Regressionslauf des `LiveRoomManager`;
- mit `--signalr`: Login, HTTP-Raumerstellung, SignalR, vollständiger Progress-Fan-out, Reconnect und Health-Endpunkte.

## Schnellstart

```powershell
.\.dotnet\dotnet.exe run --project tools\KeyWars.LoadTest -c Release --no-build -- 2 25 64
.\.dotnet\dotnet.exe run --project tools\KeyWars.LoadTest -c Release --no-build -- --self-test
```

Für den Netzwerk-Smoke zuerst KeyWars mit einem **temporären** Development-Datenverzeichnis starten:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5191'
$env:KEYWARS__DATA__DIRECTORY = Join-Path $env:TEMP ('keywars-load-' + [guid]::NewGuid().ToString('N'))
$env:KEYWARS__LIVE__COUNTDOWN_SECONDS = '1'
.\.dotnet\dotnet.exe src\KeyWars\bin\Release\net10.0\KeyWars.dll
```

In einem zweiten Terminal:

```powershell
.\.dotnet\dotnet.exe tools\KeyWars.LoadTest\bin\Release\net10.0\KeyWars.LoadTest.dll `
  --signalr --scenario smoke --base-url http://127.0.0.1:5191 `
  --rooms 2 --participants 3 --steps 10 --typing-cps 5 --jitter-ms 20 `
  --reconnect-percent 34 --target-process-id <PID> `
  --json artifacts\signalr-load.json
```

Exitcode `0` bedeutet SLO bestanden, `2` SLO verletzt und `1` ungültige Konfiguration oder technischer Abbruch. `Strg+C` beendet den Lauf kontrolliert; `--timeout-seconds` setzt zusätzlich ein hartes Gesamtlimit.

## Profile und Messmodell

| Profil | Zweck | Voreinstellung |
| --- | --- | --- |
| `smoke` | lokaler/CI-Funktionsnachweis | 1 Raum × 2 Personen, 8 Schritte |
| `ramp` | Verbindungsaufbau und Aufwärmen | 5 × 10, 30 s Ramp |
| `steady` | stabile mittlere Last | 10 × 20, 300 Schritte |
| `soak` | Speicher-, Queue- und GC-Trends | 20 × 25, etwa 30 min |
| `spike` | gleichzeitiger Verbindungs-/Raum-Peak | 20 × 25, keine Ramp |

`--typing-cps` und `--jitter-ms` bilden Tippgeschwindigkeit und Streuung ab. Der Test wartet je Schritt auf **jeden erwarteten Empfänger**. Eine fehlende Zustellung zählt als Fehler; die frühere reine First-Receiver-Messung ist damit ausgeschlossen. `--reconnect-percent` führt zur Laufmitte Stop/Start/Join-Zyklen aus.

Latenzen werden getrennt pro Operation als p50/p95/p99/Maximum ausgewiesen. Pro Operation werden höchstens `--metric-capacity` Stichproben gehalten; Gesamt- und Fehlerzähler bleiben vollständig. Der Bericht enthält außerdem:

- Fan-out erwartet/beobachtet/fehlend;
- Health-Snapshots je Base-URL;
- CPU, RSS, Peak-RSS und Threads des Lastgenerators;
- Allokationen, Gen0/1/2, GC-Pausen und ThreadPool des Lastgenerators;
- bei `--target-process-id` CPU, RSS und Threads eines lokalen Zielprozesses.

Remote-Ziel-GC ist von außen nicht seriös messbar. Dafür müssen Laufzeitmetriken des Zielclusters verwendet werden.

Der Performance-Workflow startet zusätzlich PostgreSQL, Redis, zwei Web- und
zwei Arena-Replikate über den Compose-Test-Override. Er verlangt vollständigen
SignalR-Fan-out, stoppt danach eine Arena-Replik und wiederholt den Smoke gegen
die verbleibende Replik. Protokolle und JSON-Messwerte werden immer als
Workflow-Artefakt gesichert.

## SLO-Grenzen

Der kurze CI-Gate nutzt wegen schwankender Shared Runner großzügige Grenzen: Operation p95 ≤ 2 s, p99 ≤ 5 s, Fan-out p95 ≤ 3 s, Fehlerrate 0 %, fehlende Broadcasts 0.

Für eine feste Referenzumgebung ist folgende Start-Baseline sinnvoll:

- Hub-/HTTP-Operation p95 ≤ 250 ms, p99 ≤ 1 s;
- Empfänger-Fan-out p95 ≤ 500 ms, p99 ≤ 1,5 s;
- fehlende Empfängerzustellungen und gedroppte Progress-Einträge: 0;
- technische Fehlerrate im Steady-Lauf ≤ 0,1 %;
- Ziel-CPU im Steady-Fenster ≤ 70 %, kein anhaltendes ThreadPool-Starvation-Signal;
- RSS erreicht im Soak-Lauf ein Plateau; Queue-Auslastung bleibt dauerhaft unter 70 %.

Die Latenz-/Fehlergrenzen sind direkt über `--slo-p95-ms`, `--slo-p99-ms`, `--slo-fanout-p95-ms`, `--slo-error-rate-percent` und `--slo-missing-broadcasts` ausführbar. Ressourcen- und Queue-Grenzen brauchen Zielsystem-Telemetrie und werden im Abnahmeprotokoll bewertet.

## Stufenweise Kapazitätsabnahme

| Stufe | Lastform | Nachweis |
| --- | --- | --- |
| 2 Personen | 1 Raum, `smoke` | Funktionspfad, vollständiger Fan-out, Reconnect |
| 200 Personen | z. B. 20 Räume × 10, `ramp` + `steady` | SLOs, CPU/RSS/GC, Queue- und SQLite-Verhalten |
| 20.000 Personen | viele kleine Räume, mehrere Generatoren und Replikas | Ramp, 30-min-Steady, Spike, 2-h-Soak, Replica-Ausfall |
| darüber | aus gemessener Raumverteilung hochrechnen, dann stufenweise bestätigen | Kapazitätskurve, Kosten, Autoscaling, Fehlerbudget |

Ein Raum erzeugt beim Progress-Fan-out näherungsweise `Teilnehmende² × Tippschritte` Empfängerzustellungen. Deshalb sind „20.000 Personen in vielen kleinen Räumen“ und „20.000 Personen in einem Raum“ völlig verschiedene Lasten. Die produktive Raumgrößenverteilung muss Teil des Testprofils sein.

Für mehrere Replikas jede direkte Adresse wiederholt angeben:

```powershell
--base-url http://node-a:8080 --base-url http://node-b:8080
```

Clients werden Round-Robin verteilt. `--forced-node 0` bindet den gesamten Lauf an einen Knoten und hilft bei A/B- oder Kapazitätsvergleichen. Ein verteilter Test ist nur sinnvoll, wenn Session-/Raumzustand, Data-Protection-Schlüssel und Persistenz gemäß Scale-Modus wirklich gemeinsam nutzbar sind; der Test macht fehlende Cross-Node-Sichtbarkeit als Raum- oder Fan-out-Fehler sichtbar.

## Wichtige Grenzen

- Development-Login ist nur für isolierte Testumgebungen. Nie gegen Produktionskonten lasten.
- Servergrenzen wie maximale Räume und Personen je Raum vor dem Lauf bewusst setzen und im Bericht festhalten.
- Ab 20.000 Verbindungen mehrere Lastgeneratoren verwenden; sonst wird der Generator zum Engpass.
- Soak-, Failover- und Autoscaling-Abnahmen gehören auf dedizierte Infrastruktur, nicht in Pull-Request-CI.
- Eine fiktive Zahl von 20 Millionen gleichzeitigen Nutzenden ist **keine Garantie**. Sie erfordert verteilte Generatoren, mehrere Regionen, Kapazitäts- und Kostenmodelle sowie wiederholte Messungen jeder Ausbaustufe.
