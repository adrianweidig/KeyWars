# Test Strategy

KeyWars treats tests as part of the feature contract. New functionality is not
complete until the behavior is covered in the closest matching test layer.

| Area | Primary Test Layer | Examples |
| --- | --- | --- |
| Typing metrics, grapheme handling, ranking math | Unit tests | `TypingAndRankingTests`, `DisplayNamesTests` |
| Attempt lifecycle and persistence | Integration tests | start/begin/finish, replay protection, error storage |
| Motivation and gamification | Integration tests | XP ledger, missions, achievements, event feed, privacy reset |
| Text library | Integration tests and browser tests | visibility, organization texts, card layout |
| Challenges | Integration tests | invite, participate, finish, rating updates |
| Live arena state | Unit and concurrency tests | room codes, progress deltas, reactions, concurrent joins |
| Live arena persistence | Integration and concurrency tests | completion queue, abort handling, idempotency |
| Security headers and HTTP smoke | E2E tests | auth redirects, CSP, health endpoints |
| User-facing layout | Browser tests | desktop/mobile overflow, critical workflows |

## Coverage Rule

When a change touches a public endpoint, PageModel, service method or SignalR
message, add or update a test that would fail if the behavior regressed. If the
change is only visual, add browser coverage for layout, overflow and console
errors. If the change is a refactor, keep existing tests green and add a focused
test for any newly extracted business component.

## Local Verification

Use the repository-local .NET runtime for `.slnx` support:

```powershell
$env:DOTNET_ROOT='F:\KeyWars\.dotnet'
$env:PATH='F:\KeyWars\.dotnet;' + $env:PATH
dotnet build .\KeyWars.slnx -c Release --no-restore
dotnet test .\KeyWars.slnx -c Release --no-build --no-restore
```

Browser tests use Playwright and start the app through `tests/browser/start-keywars.mjs`:

```powershell
npm run test:browser
```
