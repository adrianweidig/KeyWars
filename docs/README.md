# KeyWars-Dokumentation

Dieser Index führt von der Installation zu Betrieb, Nutzung, Architektur und
Entwicklung. Für einen neuen Betrieb empfiehlt sich die Reihenfolge
[Konfiguration](configuration.md), [LDAP](ldap.md),
[Reverse Proxy](reverse-proxy.md), [Backup und Restore](backup-restore.md) und
[Fehlerbehebung](troubleshooting.md).

## Betrieb

- [Konfiguration](configuration.md): Umgebungsvariablen und Health-Endpunkte
- [LDAP und Active Directory](ldap.md): LDAPS, StartTLS und Nutzerbindung
- [Reverse Proxy](reverse-proxy.md): HTTPS, Forwarded Headers und WebSockets
- [Portainer](portainer.md): Stack-Bereitstellung
- [Skalierter Betrieb](scale-operations.md): Rollen, PostgreSQL, Redis und Compose
- [Air-Gap-Installation](airgap-install.md): Offline-Image und Release-Artefakte
- [Backup und Restore](backup-restore.md): Sicherung und Wiederherstellung
- [Datenlebenszyklus und Retention](retention.md): Dry-run, Schutzgrenzen und Backup-Paarpflege
- [Sicherheit](security.md): technische Sicherheitsgrenzen
- [Fehlerbehebung](troubleshooting.md): Health- und Betriebsdiagnose

## Produkt und Nutzung

- [Benutzerhilfe](user-guide.md): Bereiche und Einstiege der Oberfläche
- [Funktionsumfang](features.md): kompakte Produktsicht ohne Prüfdopplungen
- [Implementierungsstatus](implementation-status.md): technische Nachweise und offene Abnahmen
- [Motivation](motivation.md): XP, Missionen, Erfolge und Rating
- [Live-Arena](live-arena.md): Raum-, Serien- und Teammodell
- [Datenschutz](privacy.md): Export, Reset und Profillöschung

## Architektur und Daten

- [Architektur](architecture.md): Prozess-, Persistenz- und Laufzeitmodell
- [Datenmodell](data-model.md): wichtige Tabellen und Invarianten
- [Tippmetriken](typing-metrics.md): Timing, Grapheme und Fehlerauswertung

## Entwicklung und Evidenz

- [Entwicklung und Code-Navigation](development.md): Module und Fertig-Kriterien
- [Teststrategie](test-strategy.md): Testschichten und lokale Prüfungen
- [Lasttests](load-testing.md): In-Memory- und SignalR-Netzwerkmodus
- [Real-AD-E2E](real-ad-e2e.md): kontrollierte LDAPS-Abnahme
- [Implementierungsstatus](implementation-status.md): Audit-Capabilities und Evidenzgrenzen

## Architekturentscheidungen

- [ADR 0001](adr/0001-one-container-sqlite.md): historischer Einzelinstanzentscheid
- [ADR 0002](adr/0002-direct-user-bind.md): direkter Nutzer-Bind
- [ADR 0003](adr/0003-jit-provisioning.md): JIT-Provisionierung
- [ADR 0004](adr/0004-no-admin-role.md): keine Anwendungs-Adminrolle
- [ADR 0005](adr/0005-external-proxy.md): externer Reverse Proxy
- [ADR 0006](adr/0006-razor-vanilla-js.md): Razor Pages und Browser-JavaScript
- [ADR 0007](adr/0007-signalr-messagepack.md): SignalR und MessagePack
- [ADR 0008](adr/0008-challenge-participants.md): Challenge-Teilnehmermodell
- [ADR 0009](adr/0009-inmemory-room-state.md): historischer In-Memory-Raumzustand
- [ADR 0010](adr/0010-bounded-channels.md): begrenzte Progress-Deltas
- [ADR 0011](adr/0011-vertical-scaling.md): historischer Vertikalskalierungsentscheid
- [ADR 0012](adr/0012-user-text-rating.md): Bewertung eigener Texte
- [ADR 0013](adr/0013-pairwise-elo.md): paarweises Elo
- [ADR 0014](adr/0014-motivation-no-currency.md): Motivation ohne Währung
- [ADR 0015](adr/0015-no-keystroke-replays.md): keine Keystroke-Replays
- [ADR 0016](adr/0016-standalone-and-scale-mode.md): Einzelinstanz und optionaler Scale-Modus
