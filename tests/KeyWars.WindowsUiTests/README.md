# Windows-UI-Testumgebung

Diese zusätzliche Testschicht prüft KeyWars außerhalb des DOM-basierten
Playwright-Pfads mit echten Windows-Werkzeugen:

- NUnit organisiert Fixture, Lebenszyklus und Assertions;
- FlaUI mit UIA3 startet und inspiziert ein echtes Edge- oder Chrome-Fenster
  einschließlich der semantischen Anmeldeelemente;
- OpenCvSharp analysiert eine deterministische Headless-Aufnahme derselben
  Edge-/Chrome-Laufzeit auf Größe, Helligkeitsverteilung, Kontrast und
  strukturierte Kanten. Damit bleibt die Bildprüfung auch bei einer gesperrten
  Windows-Sitzung aussagekräftig.

Die Fixture startet einen isolierten KeyWars-Prozess mit temporärer
SQLite-Ablage und einem eigenen Browserprofil. Nach dem Lauf werden Prozesse
und temporäre Laufzeitdaten entfernt. Screenshots sowie Anwendungslogs bleiben
im konfigurierten Artefaktverzeichnis erhalten.

## Voraussetzungen

- Windows 10 oder neuer;
- Microsoft Edge oder Google Chrome;
- ein .NET 10 SDK im `PATH`;
- ein Release-Build des Testprojekts.

## Lokaler Lauf

```powershell
$repoRoot = (Resolve-Path .).Path
$env:KEYWARS_WINDOWS_UI_ARTIFACTS = Join-Path $repoRoot 'output/windows-ui'
dotnet restore ./KeyWars.slnx --locked-mode
dotnet build ./tests/KeyWars.WindowsUiTests/KeyWars.WindowsUiTests.csproj -c Release --no-restore
dotnet test ./tests/KeyWars.WindowsUiTests/KeyWars.WindowsUiTests.csproj -c Release --no-build --no-restore
```

Ein alternativer Browser kann über `KEYWARS_WINDOWS_UI_BROWSER` als absoluter
Pfad zu `msedge.exe` oder `chrome.exe` angegeben werden. In GitHub Actions
läuft die Testschicht ausschließlich auf einem Windows-Runner. Linux baut die
gesamte Solution, führt dieses Windows-spezifische Testprojekt aber nicht aus.
