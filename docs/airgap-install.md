# Air-Gap-Installation

1. Release-Artefakte auf einem Internetrechner laden, insbesondere `keywars-vX.Y.Z-linux-amd64.tar.gz`.
2. `SHA256SUMS` prüfen.
3. Image-Archiv sicher übertragen.
4. Auf Zielsystem importieren:

```bash
docker load -i keywars-vX.Y.Z-linux-amd64.tar.gz
```

5. Tag in `.env` oder Portainer setzen.
6. Stack starten.

Nach Installation benötigt KeyWars nur Browser/Proxy-Zugriffe und LDAP/LDAPS zu Domain Controllern.
