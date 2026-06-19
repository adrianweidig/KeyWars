# Lasttests

Das Tool `tools/KeyWars.LoadTest` hat zwei Modi.

## In-Memory-Smoke

Ohne weitere Optionen simuliert das Tool lokale Raumteilnehmer direkt gegen den
`LiveRoomManager`. Dieser Modus ist schnell und eignet sich als Entwicklungs-
Smoke, misst aber nicht SignalR, Cookies, Serialisierung oder Netzwerkpfade.

```powershell
.\.dotnet\dotnet.exe run --project tools\KeyWars.LoadTest -c Release --no-build -- 2 10 25 50 100
```

## SignalR-Netzwerkmodus

Mit `--signalr` verbindet sich das Tool mit dem offiziellen SignalR-.NET-Client
gegen eine laufende KeyWars-Instanz. Es meldet kurzlebige Development-Nutzer an,
erstellt einen internen Arena-Raum per HTTP-Formular, verbindet alle
Teilnehmenden mit `/hubs/arena`, setzt Ready, startet den Countdown, sendet
Progress, beendet die Runde und schreibt maschinenlesbare JSON-Metriken.

Die laufende Instanz muss mit `Development` und temporärem Datenverzeichnis
gestartet werden. Beispiel:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:KEYWARS__DATA__DIRECTORY=Join-Path $env:TEMP ('keywars-load-' + [guid]::NewGuid().ToString('N'))
$env:KEYWARS__LIVE__COUNTDOWN_SECONDS='1'
$env:Logging__LogLevel__Default='Warning'
$env:Logging__LogLevel__Microsoft='Warning'
$env:PATH='C:\Users\adrian.TOP\.local\nodejs\node-v24.16.0-win-x64;' + $env:PATH
node tests\browser\start-keywars.mjs 5191
```

In einem zweiten Terminal:

```powershell
.\.dotnet\dotnet.exe run --project tools\KeyWars.LoadTest -c Release --no-build -- `
  --signalr `
  --base-url http://127.0.0.1:5191 `
  --participants 2 `
  --rooms 1 `
  --steps 12 `
  --json output\playwright\signalr-load.json
```

Das JSON enthält Commit, Host, CPU-Anzahl, Runtime, Raumresultate, Command-
Latenzen, Broadcast-Latenzen, Fehlerzahl sowie die Health-Snapshots von
`/health/arena-progress` und `/health/arena-persistence`.

Der Login-Endpunkt ist absichtlich rate-limitiert. Für größere Szenarien sollte
`--login-delay-ms` gesetzt oder ein bereits dediziertes Testsystem verwendet
werden. Die Pflichtszenarien 2, 10, 25, 50 und 64 Teilnehmende sowie 1, 5 und
20 parallele Räume müssen auf Referenzhardware als Betreiber- oder
Release-Artefakt ausgeführt und verglichen werden.

Aktuell deckt der SignalR-Modus den echten Hubpfad für Join, Ready, Start,
Progress und Finish ab. Lange Soak-Läufe, Fault Injection für Netzwerk/DB/Proxy,
Shutdown/Restart-Tests, CPU/RAM/GC-Auswertung und Regression gegen historische
Baselineberichte bleiben separate Betreiberabnahmen.
