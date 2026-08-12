# KeyWars

[![CI](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/ci.yml)
[![Container](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml/badge.svg?branch=master)](https://github.com/adrianweidig/KeyWars/actions/workflows/container.yml)
[![Release](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml/badge.svg)](https://github.com/adrianweidig/KeyWars/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

KeyWars ist ein selbst gehosteter Tipptrainer mit Team-Challenges und
SignalR-Live-Rennen. Profile entstehen nach erfolgreicher Anmeldung über
Active Directory oder LDAP; eine separate lokale Nutzerverwaltung gibt es
nicht. Im einfachen Standardbetrieb läuft die ASP.NET-Core-Anwendung in einem
Container und speichert ihre SQLite-Daten unter `/data`. Der optionale
Scale-Modus trennt Rollen und nutzt PostgreSQL sowie Redis.

## Admin-Schnellroute

Wähle zuerst die Betriebsart:

- **Einzelinstanz:** `compose.yaml`, SQLite und ein Datenvolume. Das ist der
  einfachste Weg für einen einzelnen Host.
- **Scale-Modus:** `compose.scale.yaml`, PostgreSQL, Redis und getrennte
  Laufzeitrollen. Einstieg: [Skalierter Betrieb](docs/scale-operations.md).

Danach gilt für die Einzelinstanz:

1. **Installieren:** Lade `compose.yaml` und `default.env.example` aus dem
   [aktuellen Release](https://github.com/adrianweidig/KeyWars/releases/latest),
   kopiere die Beispielkonfiguration nach `.env` und setze einen versionierten
   Image-Tag. Für abgeschottete Systeme gilt die
   [Air-Gap-Anleitung](docs/airgap-install.md).
2. **LDAP einrichten:** Trage URL, Base-DN und UPN-Suffix in `.env` ein. LDAPS
   ist der Standard; StartTLS muss ausdrücklich aktiviert werden. Details:
   [LDAP und Active Directory](docs/ldap.md).
3. **Proxy vorschalten:** Stelle extern HTTPS bereit, erhalte den Host-Header
   und aktiviere WebSocket-Upgrades für `/hubs/arena`. Details:
   [Reverse Proxy](docs/reverse-proxy.md).
4. **Backup prüfen:** Erzeuge vor Upgrades einen konsistenten Online-Snapshot
   und bewahre Datenbank und Manifest gemeinsam auf. Details:
   [Backup und Restore](docs/backup-restore.md).
5. **Betrieb prüfen:** `/health/live` prüft den Prozess, `/health/ready` die
   konfigurierte Datenbank. Weitere Diagnosepunkte stehen unter
   [Fehlerbehebung](docs/troubleshooting.md).

Minimaler Start aus den entpackten Release-Artefakten:

```bash
cp default.env.example .env
# .env bearbeiten: KEYWARS_VERSION und KEYWARS_LDAP_* setzen
docker compose --env-file .env up -d
curl --fail http://127.0.0.1:8080/health/ready
```

Ein manuelles Backup bei laufendem Container:

```bash
docker exec keywars dotnet KeyWars.dll maintenance backup
```

Der Stack enthält bewusst keinen Reverse Proxy und kein Zertifikat.

## Kernfunktionen

- Tipptraining mit serverautoritativem Timing, Genauigkeit, Fehleranalyse und
  Verlauf;
- direkte und gruppenfähige Challenges;
- Live-Arena mit klassischen Rennen, Serien- und Teamwertung;
- XP, Level, Missionen, Erfolge, Serien und Rating ohne Shop oder Währung;
- eigene Texte, Sammlungen, Profilexport, Statistik-Reset und Profillöschung;
- selbst gehosteter Betrieb ohne externe Runtime-CDNs.

Eine kompakte Übersicht steht unter
[Funktionsumfang](docs/features.md); technische Abnahmen führt der
[Implementierungsstatus](docs/implementation-status.md).

## Lokal entwickeln

Voraussetzungen sind das .NET 10 SDK sowie Node.js/npm. Die Befehle werden im
Repository-Stamm ausgeführt:

```powershell
dotnet restore ./KeyWars.slnx --locked-mode
dotnet build ./KeyWars.slnx -c Release --no-restore
dotnet test ./KeyWars.slnx -c Release --no-build --no-restore
npm run test:browser
```

Der lokale Test-Login ist ausschließlich in `Development` aktiv. Außerhalb
dieser Umgebung verlangt KeyWars eine gültige LDAP-Konfiguration. Einstieg in
Code und Tests: [Entwicklung](docs/development.md) und
[Teststrategie](docs/test-strategy.md).

## Dokumentation

Der [Dokumentationsindex](docs/README.md) führt zu Betrieb, Nutzung,
Architektur, Datenschutz, Entwicklung, Tests und Entscheidungen. Besonders
relevant für den Betrieb sind außerdem:

- [Konfiguration](docs/configuration.md)
- [Portainer](docs/portainer.md)
- [Datenschutz](docs/privacy.md)
- [Sicherheit](docs/security.md)

## Sicherheit und Lizenz

Sicherheitsprobleme bitte nicht als öffentliches Issue melden. Der Meldeweg
steht in [SECURITY.md](SECURITY.md). KeyWars steht unter der
[MIT-Lizenz](LICENSE).
