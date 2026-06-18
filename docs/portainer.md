# Portainer

1. Repository-`compose.yaml` und `.env.example` übernehmen.
2. `.env.example` als `.env` kopieren und LDAP-Werte setzen.
3. In Portainer einen Stack mit genau diesem Compose anlegen.
4. Nur den Service `keywars` starten.
5. Volume `keywars-data` regelmäßig sichern.

Der Stack enthält keinen Reverse Proxy und kein Zertifikat.
