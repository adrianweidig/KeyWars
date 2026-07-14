# Changelog

## v0.4.0 - 2026-07-14

- Add server-authoritative three- and five-round arena series with placement
  points, round wins, deterministic tie handling and one aggregated result.
- Add automatically balanced Team Alpha and Team Bravo races with a live team
  board, shared scoring, team placements and persisted team assignments.
- Expand the seeded German training library to 33 curated texts, including
  twelve military terminology texts, nine original stories and six factual
  scenarios.
- Add the EF Core migration, documentation, unit, concurrency, HTTP and browser
  coverage for the new arena formats and text catalog.

## v0.3.1 - 2026-07-13

- Handle expected challenge lifecycle errors inside the Razor Pages instead of
  routing expired or invalid actions through the global HTTP 500 page.
- Hide challenge actions that are not valid for the current participant state.
- Add HTTP regression coverage for expired challenge responses, play redirects
  and invalid challenge creation.

## v0.2.13 - 2026-07-04

- Hardened SQLite backup and restore connection strings so maintenance commands
  handle data and backup paths with connection-string separator characters.
- Added integration coverage for backup and restore paths with semicolons.
- Removed an obsolete visual requirements work note and cleaned user-facing
  documentation wording.

## v0.2.7 - 2026-06-29

- Made the visual asset manifest rebuild deterministic so
  `npm run assets:build` no longer dirties a clean checkout by refreshing only
  the manifest generation timestamp.

## v0.2.6 - 2026-06-29

- Added an offline visual asset pipeline for KeyWars with vendored source
  packages, SHA256 manifest generation, license snapshots, runtime sprite
  generation, and verification scripts.
- Added local KeyWars icon aliases, motivation visuals, app icons, empty-state
  illustrations, reward burst visuals, and Third-Party Notices for the new
  assets.
- Extended motivation API responses additively with `visualKey` and `accent`
  while keeping persisted gamification authority unchanged.
- Updated dashboard, profile goals, achievements, play completion, arena,
  rankings, texts, and app shell surfaces to use local offline assets.
- Added browser coverage for offline runtime assets, the achievement catalog,
  motivation visuals, responsive layout, and SignalR arena readiness.
- Hardened local npm and Playwright execution against older Node installations
  on the machine by routing asset and browser scripts through
  `scripts/run-modern-node.js`.
