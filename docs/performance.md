# Performance

KeyWars verspricht keine absolute Nutzerzahl unabhängig von Hardware. Das Lasttestwerkzeug misst reproduzierbar Räume mit 2, 10, 25, 50 und 100 simulierten Teilnehmenden.

```bash
dotnet run --project tools/KeyWars.LoadTest -c Release -- 2 10 25 50 100
```

Zu prüfen sind p95-Werte, vollständige Platzierungen, Speicherentwicklung und ob Kapazitätsgrenzen kontrolliert greifen.
