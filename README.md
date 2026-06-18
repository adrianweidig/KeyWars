# KeyWars

[![CI](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml)
[![CodeQL](https://github.com/adrianweidig/KeyWars/actions/workflows/codeql.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/codeql.yml)
[![Quality](https://github.com/adrianweidig/KeyWars/actions/workflows/quality.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/quality.yml)
[![Security](https://github.com/adrianweidig/KeyWars/actions/workflows/security.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/security.yml)
[![Container](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml)
[![Scorecard](https://github.com/adrianweidig/KeyWars/actions/workflows/scorecard.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/scorecard.yml)
[![Release](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml/badge.svg)](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**KeyWars ist ein selbst gehosteter Tipptrainer für Unternehmen, Schulen,
Ausbildung, IT-Teams und interne Lernplattformen.** Die Anwendung verbindet
Tipptraining, Team-Challenges und Live-Rennen mit echten Active-Directory- oder
LDAP-Identitäten.

Keine Fantasienamen. Keine lokale Schatten-Nutzerverwaltung. Keine externe
Cloud. Ein Container, SQLite-Daten unter `/data`, Login über LDAP oder LDAPS
und eine deutschsprachige Oberfläche für Menschen, die schneller, sauberer und
konzentrierter tippen wollen.

## Warum KeyWars?

Viele Tipptrainer sind Einzelspieler-Tools. KeyWars ist anders: Es bringt
Tipptraining dorthin, wo Menschen bereits zusammenarbeiten. In die Schule, in
die Ausbildung, ins Intranet, in Helpdesk-Teams, in Verwaltung, Support,
Entwicklung und alle Bereiche, in denen Tastatursicherheit jeden Tag Zeit spart.

- **Echte Identitäten:** Anmeldung über Active Directory, LDAPS oder LDAP mit
  StartTLS. Profile entstehen erst nach dem ersten erfolgreichen Login.
- **Motivation durch Wettbewerb:** Nutzer trainieren allein, fordern andere
  heraus oder starten Live-Rennen in gemeinsamen Räumen.
- **Einfach selbst hosten:** Ein produktiver Container, eine SQLite-Datenbank,
  keine externen Runtime-Assets, keine CDN-Abhängigkeit.
- **Datenschutzfreundlich betreiben:** Daten bleiben auf der eigenen
  Infrastruktur. Reverse Proxy, TLS, DNS und Backups bleiben unter eigener
  Kontrolle.
- **Deutschsprachig und fokussiert:** Kein überladener Lernplattform-Baukasten,
  sondern ein klarer Tipptrainer für messbaren Fortschritt.

## Funktionen

### Tipptraining

KeyWars bietet fokussierte Schreibübungen mit Zeitmessung, Genauigkeit,
Fehlerauswertung und Verlauf. Trainingseinheiten eignen sich für freies Üben,
Unterricht, Ausbildung, Onboarding und interne Lernziele.

### Challenges

Nutzer können andere Personen suchen und zu Tippduellen herausfordern. Da
KeyWars keine lokalen Nicknames verwendet, basieren Auswahl, Anzeige und
Ergebnisse auf den durch LDAP bestätigten Identitäten.

### Live-Arena

Die Live-Arena bringt mehrere Teilnehmende in einen gemeinsamen Raum. Fortschritt
und Status werden per SignalR aktualisiert, während die dauerhaften Ergebnisse
in SQLite gespeichert werden. Das eignet sich für Unterrichtsrunden,
Team-Events, Trainingssessions und kurze interne Wettbewerbe.

### Selbst gehosteter Betrieb

KeyWars läuft als ASP.NET-Core-Webanwendung mit Razor Pages und SignalR in
einem Prozess. Persistente Daten liegen unter `/data/keywars.db`. Für den
Produktivbetrieb werden LDAP-Pflichtvariablen validiert; ohne explizite
LDAP-Konfiguration startet die Anwendung nicht versehentlich offen.

## Für wen ist KeyWars?

KeyWars passt besonders gut, wenn du einen **self-hosted Tipptrainer** suchst
für:

- Schulen, Berufsschulen und Ausbildungszentren;
- Unternehmen mit Active Directory oder LDAP;
- interne IT-, Support-, Verwaltungs- und Office-Teams;
- Tipptraining im Intranet ohne externe Cloud;
- Wettbewerbe, Gruppenherausforderungen und Live-Rennen;
- Umgebungen, in denen echte Namen wichtiger sind als Nicknames.

## Schnellstart mit Docker

```bash
docker run -d --name keywars \
  -p 8080:8080 \
  -v keywars-data:/data \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e KEYWARS__DATA__DIRECTORY=/data \
  -e KEYWARS__LDAP__URLS='ldaps://dc01.example.local:636' \
  -e KEYWARS__LDAP__BASE_DN='DC=example,DC=local' \
  -e KEYWARS__LDAP__UPN_SUFFIX='example.local' \
  ghcr.io/adrianweidig/keywars:latest
```

Danach ist KeyWars auf Port `8080` erreichbar. In Produktion sollte die
Anwendung hinter einem Reverse Proxy mit TLS laufen. WebSocket-Weiterleitung
muss für SignalR aktiviert sein.

## Pflichtkonfiguration

Für Production sind mindestens diese Variablen erforderlich:

| Variable | Zweck |
| --- | --- |
| `KEYWARS__LDAP__URLS` | LDAP- oder LDAPS-Endpunkte, zum Beispiel `ldaps://dc01.example.local:636` |
| `KEYWARS__LDAP__BASE_DN` | Suchbasis des Verzeichnisses, zum Beispiel `DC=example,DC=local` |
| `KEYWARS__LDAP__UPN_SUFFIX` | UPN-Suffix für Logins, zum Beispiel `example.local` |

Optionale Variablen steuern unter anderem NetBIOS-Domain,
benutzerspezifische Suchbasen, CA-Zertifikate, Timeouts und StartTLS.
Details stehen in [docs/ldap.md](docs/ldap.md).

## Lokal entwickeln

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run --project src\KeyWars
```

In `Development` ist ein lokaler Test-Login für Entwickler-Szenarien aktiv.
In `Production` zählt nur die explizite LDAP-Konfiguration.

## Betrieb und Dokumentation

- [Architektur](docs/architecture.md): Container, Prozessmodell, SQLite,
  SignalR und Datenfluss.
- [LDAP-Anbindung](docs/ldap.md): LDAPS, StartTLS, Suchbasen und
  Zertifikatsoptionen.
- [Live-Arena](docs/live-arena.md): Räume, Laufzeitmodell und Kapazitäten.
- [Reverse Proxy](docs/reverse-proxy.md): TLS, Header und WebSocket-Weiterleitung.
- [Backup und Restore](docs/backup-restore.md): Sicherung der SQLite-Daten.
- [Air-Gap-Installation](docs/airgap-install.md): Betrieb in abgeschotteten
  Umgebungen.

## Was KeyWars bewusst nicht ist

KeyWars ist keine vollständige Lernplattform, kein Cloud-Dienst und keine
separate Benutzerverwaltung. Es ersetzt keine Identity-Infrastruktur, sondern
nutzt sie. Genau dadurch bleibt der Betrieb klein, nachvollziehbar und passend
für interne Umgebungen.

## Qualität

Die CI-, Security- und Container-Prüfungen sind nicht der Zweck dieses
Repositories, sondern das Fundament für einen verlässlichen Betrieb. KeyWars
soll installierbar, wartbar und nachvollziehbar bleiben: Restore, Build, Tests,
Docker-Build, CodeQL, Dependency Review, Secret Scanning, Trivy und SBOM helfen
dabei, ohne das Produktziel zu überdecken.

## Sicherheit melden

Bitte melde Sicherheitsprobleme nicht als öffentliches Issue. Nutze die
Hinweise in [SECURITY.md](SECURITY.md).

## Lizenz

KeyWars steht unter der [MIT-Lizenz](LICENSE).
