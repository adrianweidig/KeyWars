# Changelog

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
