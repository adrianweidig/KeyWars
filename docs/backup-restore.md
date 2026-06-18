# Backup und Restore

Manuell:

```bash
docker exec keywars dotnet KeyWars.dll maintenance backup
```

Restore aus `/data/backups`:

```bash
docker exec keywars dotnet KeyWars.dll maintenance restore /data/backups/keywars-YYYYMMDD-HHMMSS.db
```

Alternativ Container stoppen und das Docker-Volume sichern.
