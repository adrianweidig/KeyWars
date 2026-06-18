# Fehlerbehebung

- `/health/live` prüft den Prozess.
- `/health/ready` prüft Datenverzeichnis und SQLite, aber kein LDAP.
- Bei AD-Ausfall bleibt der Container bereit; neue Logins können fehlschlagen.
- Production startet nicht ohne LDAP-URL, Base-DN und UPN-Suffix.
- Development-Auth ist in Production blockiert.
- Live-Arena benötigt WebSocket-Weiterleitung am externen Proxy.
