# ADR 0002: Direkter LDAP-Nutzerbind

## Kontext

Es soll kein Servicekonto und keine lokale Nutzerverwaltung geben.

## Entscheidung

Der Login bindet direkt mit den eingegebenen AD-Zugangsdaten per LDAPS oder LDAP mit StartTLS.

## Konsequenzen

Passwörter werden nicht gespeichert. Noch nie angemeldete Personen sind lokal nicht sichtbar.

## Verworfene Alternativen

Servicekonto, AD-Synchronisation, OIDC, SAML und Kerberos-SSO.
