# ADR 0016: Einzelinstanz und optionaler Scale-Modus

## Kontext

KeyWars soll auf einem einzelnen Host einfach bleiben und bei Bedarf mehrere
Anwendungsinstanzen unterstützen. Mehrere Replikate dürfen keinen lokalen
SQLite-, Raum-, Sitzungs- oder SignalR-Zustand teilen müssen.

## Entscheidung

`compose.yaml` bleibt der Standard: eine Anwendung, SQLite und ein Datenvolume.

`compose.scale.yaml` ist der optionale Scale-Modus. Er trennt die Rollen `web`,
`arena`, `worker` und `migrate`, nutzt PostgreSQL als Datenbank und Redis für
Data Protection, SignalR und verteilten Laufzeitzustand. Nur `migrate` führt
Datenbankmigrationen aus. Die übrigen Rollen starten ohne ihre benötigten
PostgreSQL- und Redis-Verbindungen nicht.

Swarm und Kubernetes bilden dieselben Rollen und Konfigurationswerte ab. Sie
sind keine eigene Anwendungsarchitektur.

## Konsequenzen

Einzelinstanz und Scale-Modus bleiben administrativ über Compose prüfbar. Ein
einfaches Hochsetzen der Replica-Zahl von `compose.yaml` ist weiterhin nicht
zulässig.

Diese Entscheidung ersetzt die Beschränkung auf ausschließlich vertikale
Skalierung aus ADR 0001, ADR 0009 und ADR 0011. Deren Aussagen zum einfachen
SQLite-Standardbetrieb und zu transienten Tippdaten bleiben gültig.
