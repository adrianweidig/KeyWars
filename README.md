# KeyWars

KeyWars ist eine deutschsprachige, selbst gehostete Webanwendung für Tipptraining, Gruppenherausforderungen und Live-Rennen mit realen Active-Directory-Identitäten.

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

In Development ist der lokale Test-Login aktiv. Jeder nichtleere Benutzername mit nichtleerem Passwort erzeugt ein deterministisches Testprofil. In Production ist dieser Pfad durch die Startvalidierung gesperrt.

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
  ghcr.io/theheadless/keywars:latest
```

Reverse Proxy, DNS, TLS und WebSocket-Weiterleitung bleiben externe Betreiberaufgaben. KeyWars selbst lauscht nur per HTTP auf Port 8080.

## Pflichtvariablen für LDAP

- `KEYWARS__LDAP__URLS`
- `KEYWARS__LDAP__BASE_DN`
- `KEYWARS__LDAP__UPN_SUFFIX`

Optional sind `KEYWARS__LDAP__NETBIOS_DOMAIN`, `KEYWARS__LDAP__USER_BASE_DN`, `KEYWARS__LDAP__CA_CERTIFICATE_PATH`, Timeouts und `KEYWARS__LDAP__ALLOW_STARTTLS`.

Nutzer werden nicht aus AD importiert. Erst nach dem ersten erfolgreichen Login erscheinen sie in KeyWars-Suche und Challenge-Auswahl.
