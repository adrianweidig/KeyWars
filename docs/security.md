# Sicherheit

- keine Secrets im Repository;
- keine Passwörter in Logs oder Datenbank;
- Cookie-Auth mit HttpOnly, SameSite=Lax und Production-Secure;
- Data-Protection-Schlüssel unter `/data/dataprotection-keys`;
- Antiforgery-Cookie mit Production-Secure;
- lokale relative Auth-Redirects;
- CSP ohne externe Quellen;
- keine Adminrolle und keine lokale Nutzerverwaltung;
- Upload- und Teilnehmergrenzen.
