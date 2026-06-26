# KeyWars Agent Guide

## Repository Shape

KeyWars is a single ASP.NET Core application with Razor Pages, SignalR and
SQLite. Keep changes inside the existing layers unless a feature genuinely
needs a new boundary:

- `Domain`: entities, enums and pure typing/ranking logic.
- `Data`: EF Core context, migrations, backup and database initialization.
- `Services`: application use cases and stateful runtime coordination.
- `Infrastructure`: endpoint wiring, configuration aliases and middleware.
- `Pages`: Razor Page handlers and markup.
- `wwwroot`: browser JavaScript and CSS without external runtime CDNs.
- `tests`: unit, integration, concurrency, E2E and browser coverage.

## Change Rules

- Prefer small services with one business responsibility over growing
  orchestration classes.
- Keep XP authority in `RewardLedgerEntry`; `GamificationEvent` is only the
  private presentation feed for dashboards and profiles.
- Keep live-arena typed previews transient. Do not persist keystroke replay data.
- Use EF Core migrations for persisted schema changes. Do not edit old
  migration snapshots by hand except through a generated migration.
- Keep self-hosted operation intact: one container, SQLite under `/data`, LDAP
  required in production, no CDN dependency.

## Test Expectations

Every behavioral change needs a matching test at the lowest useful level:

- pure domain logic: `tests/KeyWars.UnitTests`;
- persistence, services and privacy/export behavior: `tests/KeyWars.IntegrationTests`;
- concurrent live-room behavior: `tests/KeyWars.ConcurrencyTests`;
- HTTP/security smoke behavior: `tests/KeyWars.E2ETests`;
- browser layout and SignalR flows: `tests/browser`.

Run these before handing off substantial changes:

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet build .\KeyWars.slnx -c Release --no-restore
dotnet test .\KeyWars.slnx -c Release --no-build --no-restore
npm run test:browser
```

Use short comments only for invariants that are easy to break accidentally.
Avoid comments that merely repeat method names or assignments.
