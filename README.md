# KeyWars

[![CI](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml)
[![CodeQL](https://github.com/adrianweidig/KeyWars/actions/workflows/codeql.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/codeql.yml)
[![Quality](https://github.com/adrianweidig/KeyWars/actions/workflows/quality.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/quality.yml)
[![Security](https://github.com/adrianweidig/KeyWars/actions/workflows/security.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/security.yml)
[![Container](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml)
[![Scorecard](https://github.com/adrianweidig/KeyWars/actions/workflows/scorecard.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/scorecard.yml)
[![Release](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml/badge.svg)](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

KeyWars ist eine deutschsprachige, selbst gehostete Webanwendung für Tipptraining, Gruppenherausforderungen und Live-Rennen mit realen Active-Directory-Identitäten.

## Qualitätsstatus

Dieses Repository ist als öffentliches Best-Practice-Repository aufgebaut:

- CI mit Restore, Formatcheck, Release-Build, Tests, Coverage-Artefakt, Docker-Build und Compose-Validierung;
- CodeQL-Analyse für C# und JavaScript/TypeScript;
- Dependabot für NuGet, GitHub Actions und Docker-Basisimages;
- Dependency Review für Pull Requests;
- Secret Scanning per TruffleHog;
- OpenSSF Scorecard;
- Container-Scan per Trivy;
- SBOM-Erzeugung als kostenloses GitHub-Actions-Artefakt;
- Markdown-, YAML-, GitHub-Actions- und Dockerfile-Linting;
- Security Policy, Contribution Guide, Code of Conduct, CODEOWNERS sowie Issue- und PR-Vorlagen.

## Architektur

- genau ein produktiver Container;
- ASP.NET Core 10, Razor Pages und SignalR in einem Prozess;
- SQLite unter `/data/keywars.db`;
- direkte Formularanmeldung gegen AD per LDAPS oder LDAP mit StartTLS;
- JIT-Provisionierung lokaler Profile anhand `objectGUID`;
- keine Adminoberfläche, keine lokale Nutzerverwaltung, keine Nicknames;
- keine externen Runtime-Assets, CDNs, Fonts oder Zusatzdienste.

## Lokal entwickeln

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run --project src\KeyWars
```

In Development ist ein lokaler Test-Login für Entwickler-Szenarien aktiv. Production startet nur mit expliziter LDAP-Konfiguration; ohne LDAP-Pflichtvariablen bricht die Startvalidierung ab.

## Docker Run

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

Reverse Proxy, DNS, TLS und WebSocket-Weiterleitung bleiben externe Betreiberaufgaben. KeyWars selbst lauscht nur per HTTP auf Port 8080.

## Pflichtvariablen für LDAP

- `KEYWARS__LDAP__URLS`
- `KEYWARS__LDAP__BASE_DN`
- `KEYWARS__LDAP__UPN_SUFFIX`

Optional sind `KEYWARS__LDAP__NETBIOS_DOMAIN`, `KEYWARS__LDAP__USER_BASE_DN`, `KEYWARS__LDAP__CA_CERTIFICATE_PATH`, Timeouts und `KEYWARS__LDAP__ALLOW_STARTTLS`.

Nutzer werden nicht aus AD importiert. Erst nach dem ersten erfolgreichen Login erscheinen sie in KeyWars-Suche und Challenge-Auswahl.

## Sicherheit melden

Bitte melde Sicherheitsprobleme nicht als öffentliches Issue. Nutze die Hinweise in [SECURITY.md](SECURITY.md).
