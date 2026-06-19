# ADR 0010: Begrenzte Progress-Deltas

## Kontext

Viele Fortschrittsmeldungen dürfen nicht unbeschränkt Speicher aufbauen.

## Entscheidung

Der heisse Progresspfad verwendet `LiveProgressBroadcaster` als begrenzten
Koaleszierer. Pro Raum wird nur das neueste Delta je Person gepuffert und
hoechstens mit `KEYWARS__LIVE__PROGRESS_BROADCAST_HZ` gesendet.
`KEYWARS__LIVE__ROOM_COMMAND_QUEUE_CAPACITY` begrenzt die Pending-Deltas; bei
Überlast werden neue nichtkritische Progress-Deltas verworfen und in
`/health/arena-progress` gezählt. Zuverlässige Commands wie Start, Finish,
Leave und Phasenwechsel bleiben direkte Hub-Commands mit Vollsnapshot.

## Konsequenzen

Die Implementierung verhindert Vollsnapshot-Broadcasts pro Keystroke und
Datenbanklast im Progresspfad. Eine vollständige serialisierte
RoomCommand-Pipeline für alle Raumcommands und echte SignalR-Soak-Evidenz
bleiben für den weiteren KW-015/KW-052-Ausbau offen.
