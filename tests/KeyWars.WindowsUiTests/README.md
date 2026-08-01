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
- das Repository-lokale .NET 10 SDK;
- ein Release-Build des Testprojekts.

## Lokaler Lauf

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
$env:KEYWARS_WINDOWS_UI_ARTIFACTS='F:\KeyWars\output\windows-ui'
dotnet restore .\KeyWars.slnx --locked-mode
dotnet build .\tests\KeyWars.WindowsUiTests\KeyWars.WindowsUiTests.csproj -c Release --no-restore
dotnet test .\tests\KeyWars.WindowsUiTests\KeyWars.WindowsUiTests.csproj -c Release --no-build --no-restore
```

Ein alternativer Browser kann über `KEYWARS_WINDOWS_UI_BROWSER` als absoluter
Pfad zu `msedge.exe` oder `chrome.exe` angegeben werden. In GitHub Actions
läuft die Testschicht auf einem Windows-Runner; auf anderen Betriebssystemen
wird sie explizit übersprungen.
