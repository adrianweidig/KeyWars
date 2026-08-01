# Entwicklung und Code-Navigation

Dieses Dokument hilft beim manuellen Bearbeiten von KeyWars. Es beschreibt,
wo Änderungen hingehören, welche Dateien zusammenwirken und welche Prüfungen
vor einem Pull Request sinnvoll sind.

## Schnellstart

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet restore .\KeyWars.slnx --locked-mode
dotnet build .\KeyWars.slnx -c Release --no-restore
dotnet test .\KeyWars.slnx -c Release --no-build --no-restore
npm run test:browser
```

Die lokale Anwendung startet mit:

```powershell
dotnet run --project .\src\KeyWars\KeyWars.csproj
```

Der Entwicklungslogin ist ausschließlich für `Development` vorgesehen.
Produktionskonfigurationen müssen LDAP oder Active Directory verwenden.

## Modulkarte

| Bereich | Verantwortung | Typische Änderungen |
| --- | --- | --- |
| `Domain` | Entitäten, Enums und reine Berechnungen | Tippmetriken, Ranking, XP-Regeln |
| `Data` | EF Core, Migrationen, Initialisierung und Backups | Schema, Abfragen, Datenpflege |
| `Services` | Anwendungsfälle und Laufzeitkoordination | Challenges, Profile, Arena, Motivation |
| `Infrastructure` | Endpunkte, Middleware und Konfigurationsbindung | HTTP, Health, Sicherheitsheader |
| `Pages` | Razor-Handler und serverseitiges Markup | Seitenabläufe, Formulare, Validierung |
| `wwwroot/js` | Browserzustand und Interaktionen | Tippen, Arena, Navigation |
| `wwwroot/css` | Geordnete visuelle Kaskade | Layout, Komponenten, Breakpoints, Themes |
| `tests` | Unit-, Integrations-, Nebenläufigkeits-, HTTP- und Browsertests | Verhaltensnachweis |

Persistierte Regeln beginnen in `Domain` oder `Services`, nicht in Razor Pages
oder JavaScript. Seiten und Browsercode stellen den serverseitigen Zustand dar,
erfinden aber keine eigene XP-, Ranking- oder Persistenzlogik.

## Live-Arena

Die Arena ist nach Verantwortung aufgeteilt:

- `LiveRoomContracts.cs`: öffentliche Request-, Snapshot- und
  Persistenzverträge;
- `LiveRoomState.cs`: ausschließlich interner, synchronisierter
  In-Memory-Zustand;
- `LiveRoomProgress.cs`: Eingabegrenzen, Graphemfortschritt, WPM, Genauigkeit
  und Ranghinweise;
- `LiveRoomScoring.cs`: Runden-, Serien- und Teamwertung;
- `LiveRoomManager.cs`: Raumlebenszyklus, Synchronisation und Orchestrierung;
- `LiveRoomCompletionQueue.cs`: zuverlässige Übergabe an die Persistenz.

Neue Arena-Regeln gehören möglichst in die fachlich passende Hilfsklasse. Der
Manager sollte keine neue Berechnungslogik aufnehmen, wenn sie ohne Zugriff auf
seine Dictionaries oder Sperren auskommt.

## Browsermodule

- `site.js` aktiviert die Seitenmodule.
- `typing.js` steuert eine lokale Trainingssitzung.
- `typing-text.js` enthält gemeinsame Unicode- und DOM-Helfer für Tipptexte.
- `arena.js` koordiniert den Zustand einer Arena-Seite.
- `arena-view.js` formatiert Arena-Zustände und erzeugt DOM-Fragmente.
- `signalr-connection.js` kapselt Aufbau und Wiederverbindung von SignalR.
- `typing-scroll.js` hält das aktuelle Zeichen im sichtbaren Bereich.

Browsermodule verwenden explizite relative Imports. Gemeinsame Helfer werden
nicht in mehreren Featuredateien kopiert. Serverseitig gelieferte Objekte werden
am Eingang normalisiert und danach intern mit camelCase verwendet.

## CSS-Kaskade

`site.css` ist historisch gewachsen und wird bewusst in Quellreihenfolge
ausgewertet. Abschnittskommentare markieren Basisregeln, Produktoberflächen,
den authentifizierten App-Rahmen, responsive Korrekturen, Fokusmodus und die
abschließenden Light-Theme-Overrides.

Vor einer CSS-Änderung:

1. den vorhandenen Selektor vollständig suchen;
2. prüfen, ob eine spätere Regel ihn überschreibt;
3. die fachlich zuständige Regel ändern, statt einen weiteren Override am
   Dateiende anzuhängen;
4. Desktop, Tablet, Mobilansicht, 200-Prozent-Zoom und beide Themes prüfen.

Eine spätere Aufteilung in mehrere Stylesheets muss die aktuelle Reihenfolge
erhalten und wird als eigener, visuell vollständig geprüfter Umbau behandelt.

## Kommentare und Benennung

- Namen beschreiben fachliche Absicht; Abkürzungen werden nur für etablierte
  Begriffe wie WPM, XP, LDAP oder HTTP verwendet.
- Kommentare erklären Invarianten, Nebenläufigkeit, Datenschutzgrenzen oder
  eine nicht offensichtliche technische Entscheidung.
- Kommentare wiederholen keine Zuweisung und ersetzen keine klare Methode.
- Öffentliche Verträge erhalten XML-Dokumentation, wenn Aufrufer Vorbedingungen
  oder Seiteneffekte nicht aus Typen und Namen erkennen können.
- Es werden keine spekulativen `TODO`-Kommentare ohne nachvollziehbaren Issue-
  oder Entscheidungsbezug hinterlassen.

## Persistenzänderungen

Schemaänderungen erfolgen ausschließlich über neue EF-Core-Migrationen. Alte
Migrationen, Designerdateien und `KeyWarsDbContextModelSnapshot.cs` werden nicht
manuell umgeschrieben. Vor einem Merge sind Upgrade, leere Initialisierung und
Backup-/Restore-Auswirkungen zu prüfen.

## Passende Testschicht

| Änderung | Mindestens ausführen |
| --- | --- |
| Reine Domainregel | `KeyWars.UnitTests` |
| Service oder Datenzugriff | `KeyWars.IntegrationTests` |
| Arena-Sperren oder parallele Zustände | `KeyWars.ConcurrencyTests` |
| HTTP, Authentifizierung oder Header | `KeyWars.E2ETests` |
| Browserinteraktion oder Layout | `npm run test:browser` |
| Windows-Fenster oder visuelle Erkennung | `KeyWars.WindowsUiTests` |

Die vollständige Matrix steht in [test-strategy.md](test-strategy.md).

## Fertig-Kriterien

- Änderung liegt in der zuständigen Schicht.
- Öffentliche Verträge und Konfiguration bleiben kompatibel oder sind
  ausdrücklich dokumentiert.
- Passende Tests decken das Verhalten auf der niedrigsten sinnvollen Ebene ab.
- Build, Formatierung und betroffene Browserprüfungen sind grün.
- Dokumentation, Release Notes und Containerbetrieb wurden bei relevanten
  Änderungen mitbetrachtet.
- Der Worktree enthält keine Datenbanken, Logs, Screenshots, Secrets oder
  generierten Archive.
