# Real-AD-E2E-Abnahme

Der Real-AD-Test läuft bewusst nicht in der öffentlichen CI, weil er echte
LDAPS-Erreichbarkeit, autorisierte Testkonten und eine laufende
Production-Instanz benötigt. Er ist für geschützte Betreiberumgebungen gedacht
und schreibt nur bereinigte Evidenz unter `output/playwright/real-ad/`.

## Voraussetzungen

- KeyWars läuft in `Production` mit echter LDAP-/LDAPS-Konfiguration.
- Der Browser erreicht die Instanz über den gleichen Proxy-Kontext wie im
  Betrieb. Bei lokalen SSH-Tunneln müssen `X-Forwarded-Proto: https` und
  `X-Forwarded-For` gesetzt werden.
- Zwei aktive AD-Testnutzer und optional ein deaktivierter AD-Testnutzer sind
  für die Dauer der Abnahme vorhanden.
- Temporäre AD-Testnutzer werden nach der Abnahme wieder gelöscht.

## Umgebungsvariablen

```powershell
$env:KEYWARS_REAL_AD_BASE_URL='http://127.0.0.1:18080'
$env:KEYWARS_REAL_AD_HOST_USER='keywars.e2e.host@top.secret'
$env:KEYWARS_REAL_AD_HOST_PASSWORD='<geschützt>'
$env:KEYWARS_REAL_AD_GUEST_USER='keywars.e2e.guest@top.secret'
$env:KEYWARS_REAL_AD_GUEST_PASSWORD='<geschützt>'
$env:KEYWARS_REAL_AD_DISABLED_USER='keywars.e2e.disabled@top.secret'
$env:KEYWARS_REAL_AD_DISABLED_PASSWORD='<geschützt>'
$env:KEYWARS_REAL_AD_FORWARDED_PROTO='https'
$env:KEYWARS_REAL_AD_FORWARDED_FOR='127.0.0.1'
$env:KEYWARS_REAL_AD_REPORT='output\playwright\real-ad\keywars-real-ad-report.json'
```

`KEYWARS_REAL_AD_DISABLED_*` ist optional. Wenn die Werte fehlen, wird nur die
Disabled-Account-Prüfung übersprungen; die Zwei-Nutzer-Arena bleibt weiterhin
verpflichtend.

## Ausführung

```powershell
npm run test:real-ad
```

Der Test prüft:

- falsches Passwort wird abgelehnt;
- deaktivierter AD-Nutzer wird abgelehnt, falls konfiguriert;
- zwei echte AD-Nutzer melden sich an;
- Profile entstehen per LDAP-`objectGUID`;
- ein Arena-Raum wird erstellt und per Code betreten;
- doppelte Gast-Tabs erzeugen keinen zweiten Teilnehmer;
- Reload/Reconnect zeigt die bestehende Teilnahme;
- beide Nutzer absolvieren eine Runde bis zum Podium.

Nach einem erfolgreichen Lauf sollte zusätzlich eine DB-Evidenz aus der
Produktionsdatenbank gesichert werden: aktueller Raum, zwei teilnehmende
Profile, unterschiedliche `DirectoryObjectGuid`-Werte und zwei
`Finished`-Teilnehmer mit Rating-Auditwerten.
