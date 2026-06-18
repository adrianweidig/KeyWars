# Fehlerbehebung

- `/health/live` prüft den Prozess.
- `/health/ready` prüft Datenverzeichnis und SQLite, aber kein LDAP.
- `/health/arena-persistence` zeigt Pending Jobs, Queue-Kapazität und Fehlversuche der Arena-Abschlusspersistenz.
- `/health/arena-progress` zeigt koaleszierte und verworfene Progress-Deltas sowie Broadcast-Zählwerte.
- Bei AD-Ausfall bleibt der Container bereit; neue Logins können fehlschlagen.
- Production startet nicht ohne LDAP-URL, Base-DN und UPN-Suffix.
- Development-Auth ist in Production blockiert.
- Live-Arena benötigt WebSocket-Weiterleitung am externen Proxy.
