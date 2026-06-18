# LDAP und Active Directory

Production benötigt:

```env
KEYWARS__LDAP__URLS=ldaps://dc01.example.local:636
KEYWARS__LDAP__BASE_DN=DC=example,DC=local
KEYWARS__LDAP__UPN_SUFFIX=example.local
```

Mehrere Domain Controller werden mit Semikolon getrennt. `ldap://` ist nur
erlaubt, wenn `KEYWARS__LDAP__ALLOW_STARTTLS=true` gesetzt ist. Es gibt kein
Servicekonto und keine Gruppenzulassung.

Optionale TLS- und Timeout-Variablen:

```env
KEYWARS__LDAP__CA_CERTIFICATE_PATH=/data/certs/ad-root-ca.pem
KEYWARS__LDAP__CONNECT_TIMEOUT_SECONDS=5
KEYWARS__LDAP__OPERATION_TIMEOUT_SECONDS=10
```

Wenn `KEYWARS__LDAP__CA_CERTIFICATE_PATH` gesetzt ist, muss die Datei beim
Start existieren. KeyWars baut dann eine Zertifikatskette gegen diese Root-CA
und prueft den Hostnamen des Domain Controllers. Ohne diese Variable gilt die
Zertifikatspruefung des Betriebssystems.

Ablauf:

1. Benutzername wird normalisiert.
2. Verbindung zu einem konfigurierten Domain Controller wird aufgebaut.
3. Bind erfolgt mit Nutzername und Passwort.
4. Mit derselben Verbindung wird das eigene Benutzerobjekt gesucht.
5. `objectGUID`, `objectSid`, `sAMAccountName`, `userPrincipalName`, Anzeigename und optionale Stammdaten werden gelesen.
6. Das lokale Profil wird anhand `objectGUID` erstellt oder aktualisiert.

Das Passwort wird weder gespeichert noch geloggt.
