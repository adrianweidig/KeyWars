# Mitwirken

Danke, dass du KeyWars verbesserst. Die ausführliche Modulkarte und Hinweise
zur Code-Navigation stehen in [docs/development.md](docs/development.md).

## Entwicklungsumgebung

Voraussetzungen sind das .NET 10 SDK sowie Node.js/npm im `PATH`. Führe die
Befehle im Repository-Stamm aus:

```powershell
dotnet restore ./KeyWars.slnx --locked-mode
dotnet build ./KeyWars.slnx -c Release --no-restore
dotnet test ./KeyWars.slnx -c Release --no-build --no-restore
dotnet format ./KeyWars.slnx --verify-no-changes --no-restore
npm run test:browser
```


Nutze für eine Änderung die kleinste passende Testschicht. Browser-, Layout-
und Interaktionsänderungen benötigen zusätzlich eine gerenderte Prüfung.

## Codeänderungen

- Lege Fachlogik in `Domain` oder einem klar zuständigen Service ab.
- Halte Razor-Handler und Browsermodule frei von eigener XP-, Ranking- oder
  Persistenzautorität.
- Extrahiere gemeinsam genutzte Logik, statt sie zwischen Featuredateien zu
  kopieren.
- Kommentiere Invarianten und nicht offensichtliche Entscheidungen, nicht den
  unmittelbar lesbaren Programmablauf.
- Erzeuge für Schemaänderungen eine neue EF-Core-Migration; ändere bestehende
  Migrationen und Snapshots nicht von Hand.

## Pull Requests

- Halte Änderungen fokussiert und beschreibe betriebliche Auswirkungen.
- Aktualisiere Dokumentation bei Verhaltens-, Konfigurations- oder
  Deploymentänderungen.
- Ergänze Tests auf der niedrigsten sinnvollen Ebene.
- Committe keine Archive, Datenbanken, Logs, Secrets oder lokale
  Laufzeitdateien.
- Produktionsauthentifizierung bleibt an LDAP oder Active Directory gebunden;
  der Entwicklungslogin darf nicht in Produktion aktiv werden.
- Prüfe bei Docker- oder Releaseänderungen GHCR, Offline-Artefakte und
  Release Notes mit.

## Commit-Stil

Verwende kurze, imperative Commit-Nachrichten, zum Beispiel:

```text
Arena-Wertung in eigenes Modul verschieben
LDAP-Startvalidierung korrigieren
```
