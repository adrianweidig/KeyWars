# Datenlebenszyklus und Retention

KeyWars führt Datenpflege standardmäßig nur als **Dry-run** aus. Automatische
Läufe sind standardmäßig deaktiviert. Ein schreibender manueller Lauf muss
explizit mit `--apply` gestartet werden:

```bash
docker exec keywars dotnet KeyWars.dll maintenance retention
docker exec keywars dotnet KeyWars.dll maintenance retention --apply
```

Die Datenbank-Retention unterstützt den Standalone-SQLite-Modus und den
PostgreSQL-Scale-Modus. Im Scale-Modus wird der Hosted Service ausschließlich
in der Rolle `worker` oder `all` registriert. Alle berechtigten Replikate
versuchen pro Zyklus denselben erneuerbaren Redis-Lease zu erwerben; genau eine
führt den Lauf aus, die übrigen überspringen ihn. Web- und Arena-Replikate
starten keinen zusätzlichen Retention-Worker.

Der Dry-run gibt dieselben Grenzen und Kandidatenzahlen aus, ändert aber weder
Datenbankzeilen noch Backup-Dateien. Ein externer SQLite-`--apply`-Lauf benötigt
den exklusiven KeyWars-Runtime-Lock; dafür muss der Webcontainer gestoppt sein.
PostgreSQL kann die kleinen, erneut geprüften Batches online verarbeiten. Der
integrierte Hosted Service läuft im SQLite-Modus in der `all`-Instanz und im
Scale-Modus im Worker, der den Cluster-Lease erhalten hat. Ein schreibender
PostgreSQL-CLI-Lauf verwendet denselben Lease und bricht verständlich ab, wenn
bereits ein Worker Retention ausführt.

SQLite vergleicht die bestehende kanonische UTC-Textdarstellung sekundengenau.
PostgreSQL verwendet native `DateTimeOffset`-/`timestamptz`-Bereiche. Dadurch
bleibt der PostgreSQL-Pfad über native Zeitindizes optimierbar, während SQLite
die dort vorhandene Textpersistenz ohne unsichere Offset-Sortierung auswertet.

## Schutzgrenzen

Ein Lauf darf ausschließlich:

- vorbereitete oder gestartete Versuche nach Ablauf des Sessionfensters als
  `Expired` markieren;
- überfällige offene oder laufende Challenges als `Expired` markieren;
- alte, nicht abgeschlossene `Expired`-/`Aborted`-Versuche löschen, wenn weder
  eine Challenge-Bindung noch eine XP-Ledgerbuchung auf den Versuch zeigt;
- alte bereits gesehene `GamificationEvents` löschen;
- vollständige Backup-Paare aus Datenbank und Manifest entfernen.

`RewardLedgerEntries` werden nie durch Retention gelöscht. Abgeschlossene
Tippversuche, Arena-/Challenge-Ergebnisse, Missionen und Achievements bleiben
ebenfalls erhalten. Ungesehene Gamification-Ereignisse werden nicht gelöscht.

Jede Kategorie arbeitet in Batches. Pro Lauf werden höchstens
`BATCH_SIZE * MAX_BATCHES_PER_RUN` Datensätze je Kategorie verändert. Der
Ergebnisbericht zeigt verbleibende Kandidaten und ob die Laufgrenze erreicht
wurde.

## Backup-Paare

Das Erstellungsdatum aus dem validierten Manifest bestimmt das Alter. Normale
Backups und Pre-Restore-Backups bilden getrennte Familien; die konfigurierte
Mindestzahl bleibt in jeder vorhandenen Familie erhalten. Fehlende, ungültige
oder symbolisch verknüpfte Dateien werden gemeldet und nie automatisch
gelöscht. Datenbank und `.manifest.json` werden nur als gemeinsam validiertes
Paar ausgewählt.

Backups müssen weiterhin auf unabhängigen Speicher exportiert werden. Lokale
Retention ersetzt weder einen Backup-Zeitplan noch einen Restore-Test.
Die Backup-Paarretention ist ausschließlich im SQLite-Modus anwendbar. Ein
PostgreSQL-Bericht kennzeichnet diesen Schritt explizit als nicht anwendbar;
PostgreSQL-Backups und deren Aufbewahrung gehören in `pg_dump`-/`pg_restore`-
beziehungsweise Plattform-Backupprozesse.

## Konfiguration

| `.env`-Variable | Standard | Bedeutung |
| --- | ---: | --- |
| `KEYWARS_RETENTION_ENABLED` | `false` | Hosted Service aktivieren |
| `KEYWARS_RETENTION_DRY_RUN` | `true` | Hosted Service ohne Änderungen ausführen |
| `KEYWARS_RETENTION_INTERVAL_HOURS` | `24` | Abstand automatischer Läufe |
| `KEYWARS_RETENTION_BATCH_SIZE` | `250` | Datensätze je Batch |
| `KEYWARS_RETENTION_MAX_BATCHES_PER_RUN` | `20` | maximale Batches je Kategorie und Lauf |
| `KEYWARS_RETENTION_STALE_ATTEMPT_HOURS` | `2` | Ablaufgrenze aktiver Versuche; Minimum 2 |
| `KEYWARS_RETENTION_ABANDONED_ATTEMPT_DAYS` | `90` | Aufbewahrung unvollständiger terminaler Versuche |
| `KEYWARS_RETENTION_SEEN_EVENT_DAYS` | `180` | Aufbewahrung bereits gesehener Präsentationsereignisse |
| `KEYWARS_RETENTION_BACKUP_DAYS` | `30` | Mindestalter löschbarer lokaler Backup-Paare; nur SQLite |
| `KEYWARS_RETENTION_MINIMUM_BACKUP_PAIRS` | `3` | je Backup-Familie immer zu erhaltende Paare; nur SQLite |

Für den ersten produktiven Einsatz zunächst den Dry-run aktivieren, den Bericht
und den externen Backup-Export prüfen und erst anschließend `DRY_RUN=false`
setzen.

## Integrationstest

Der SQLite-Pfad läuft in den normalen Integrationstests. Der PostgreSQL-Pfad
verwendet ein zufällig benanntes, nach dem Test wieder entferntes Schema und
wird aktiviert, wenn `KEYWARS_TEST_POSTGRES_CONNECTION_STRING` auf eine
ausschließlich für Tests vorgesehene Datenbank mit Schema-Rechten zeigt. Ohne
diese Variable wird der PostgreSQL-Test als übersprungen gemeldet.
