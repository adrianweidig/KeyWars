# Teststrategie

Tests sind Teil des Funktionsvertrags. Neues Verhalten wird in der niedrigsten
sinnvollen Schicht abgesichert.

| Änderung | Primäre Testschicht |
| --- | --- |
| Tippmetriken, Grapheme, Rating und reine Regeln | `KeyWars.UnitTests` |
| Persistenz, Services, Datenschutz und Motivation | `KeyWars.IntegrationTests` |
| parallele Arena-Zustände und Sperren | `KeyWars.ConcurrencyTests` |
| HTTP, Authentifizierung und Sicherheitsheader | `KeyWars.E2ETests` |
| Browserabläufe, Layout und Accessibility | `tests/browser` mit Playwright |
| echtes Windows-Fenster und Bilderkennung | NUnit, FlaUI und OpenCV |

## Regel

Ändert sich ein Endpunkt, PageModel, Servicevertrag oder SignalR-Ereignis,
muss ein Test die Regression sichtbar machen. Reine Layoutänderungen benötigen
Browserabdeckung für Darstellung, Überlauf und Konsolenfehler. Ein Refactoring
behält vorhandene Tests und ergänzt nur neue fachliche Grenzen.

## Ausführen

Der vollständige lokale Standardlauf steht einmalig unter
[Entwicklung: Schnellstart](development.md#schnellstart).

Windows-UI-Tests starten einen echten Edge- oder Chrome-Prozess und laufen in
CI nur unter Windows:

```powershell
$repoRoot = (Resolve-Path .).Path
$env:KEYWARS_WINDOWS_UI_ARTIFACTS = Join-Path $repoRoot 'output/windows-ui'
dotnet build ./tests/KeyWars.WindowsUiTests/KeyWars.WindowsUiTests.csproj -c Release --no-restore
dotnet test ./tests/KeyWars.WindowsUiTests/KeyWars.WindowsUiTests.csproj -c Release --no-build --no-restore
```

Voraussetzungen und optionale Pfade stehen in
[`tests/KeyWars.WindowsUiTests/README.md`](../tests/KeyWars.WindowsUiTests/README.md).
