# Sicherheit

- keine Secrets im Repository;
- keine Passwörter in Logs oder Datenbank;
- Cookie-Auth mit HttpOnly, SameSite=Lax und Production-Secure;
- gemeinsamer Data-Protection-Schlüsselring: unter `/data` in der
  Einzelinstanz, über Redis im Scale-Modus;
- Antiforgery-Cookie mit Production-Secure;
- lokale relative Auth-Redirects;
- CSP ohne externe Quellen;
- keine lokale Adminrolle und keine lokale Nutzerverwaltung;
- Content-Moderation nur über einen beim LDAP-Login ausgestellten, signierten
  Gruppen-Claim und eine eigene Autorisierungsrichtlinie;
- fremde organisationsweite Inhalte können nur mit Begründung moderiert werden;
  Akteur, Ziel, Aktion und Zeitpunkt landen in einer unveränderlichen Auditspur;
- Upload- und Teilnehmergrenzen.
