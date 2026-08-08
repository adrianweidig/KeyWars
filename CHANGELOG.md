# Changelog

## v0.4.9 - 2026-08-08

- Restrict visual-asset downloads and every redirect to reviewed HTTPS hosts,
  with focused regression coverage for the CodeQL substring bypasses.

## v0.4.8 - 2026-08-08

- Keep multi-round arena series operable after a host disconnect, broadcast
  grace-timeout transitions and cover the handoff with concurrency and real
  two-browser tests.
- Add a four-context 2-vs-2 browser race with desktop and mobile visual
  evidence, synchronized participant counts and performance timings.
- Bound normalized arena targets by graphemes and UTF-8 payload size, filter
  unsafe choices and reject manipulated submissions with HTTP regression
  coverage.
- Reduce large typing-alignment memory usage, collapse the 90-day profile
  activity view to grouped queries and remove a redundant database lookup from
  the SignalR progress hot path.
- Add an active Windows UI test that logs in and navigates through a real
  browser with NUnit, FlaUI/UIA3, OpenCV change detection and reproducible
  screenshots.
- Harden locked CI and release builds, OCI metadata, multi-architecture GHCR
  publication and independently validated offline release artifacts.
- Refresh the vendored SignalR client to 10.0.11 and replace long operator
  notes with concise installation, LDAP, Portainer, backup and troubleshooting
  guides.

## v0.4.7 - 2026-08-01

- Publish the example environment file under the stable
  `default.env.example` asset name and cover that exact downloaded name in the
  release manifest, checksums, validator and air-gap instructions.

## v0.4.6 - 2026-08-01

- Split the live-room backend into explicit contracts, synchronized state,
  progress calculations and scoring modules while keeping orchestration in the
  room manager.
- Extract shared typing, arena view and SignalR browser helpers to remove
  duplicated Unicode and connection logic.
- Add a German development guide with module ownership, CSS cascade navigation,
  comment conventions and a test-layer map for human contributors.

## v0.4.5 - 2026-08-01

- Refresh the pinned Markdown lint, Actions lint, GitHub Release and OpenSSF
  Scorecard actions after the v0.4.4 release validation.

## v0.4.4 - 2026-08-01

- Add a Windows UI test layer with NUnit, FlaUI/UIA3, OpenCV screenshot
  analysis, isolated application and browser processes, diagnostics and CI
  artifacts.
- Make the 200 percent browser zoom assertion verify horizontal reflow and
  rendered dimensions without browser-dependent coordinate spaces.
- Refresh direct .NET, SQLite, Playwright, Docker base image, Trivy and GitHub
  Actions dependencies and regenerate all locked dependency graphs.
- Group Dependabot updates by ecosystem and add npm coverage to prevent a new
  backlog of stale one-package pull requests.
- Publish curated release notes as a checksummed release artifact and use them
  as the GitHub Release description.

## v0.4.3 - 2026-07-14

- Restore clear Light Theme contrast for finish summaries, score cards and
  result metrics across dashboard, training and challenge completion views.

## v0.4.2 - 2026-07-14

- Keep dashboard round statistics on one clearly labeled attempt scope and
  update every value immediately after the current round finishes.
- Distinguish a valid timed-sprint finish from a fully and correctly completed
  target so partial sprints no longer receive misleading success copy.
- Explain WPM, accuracy and consistency evidence in the result, expose final
  error characters and suppress consistency percentages without enough timing
  samples.

## v0.4.1 - 2026-07-14

- Make every training and arena typing surface the full-width visual focus while
  keeping a comfortable centered reading measure on large displays.
- Replace parallel Sprint and Words typing cards with accessible selectors and
  one active workspace, using validated query parameters with stable defaults.
- Move dashboard rankings and training-mode navigation below the primary typing
  surface and extend responsive browser coverage across desktop and mobile.
- Widen the authenticated desktop workspace, add a persistent collapsible
  sidebar and provide a distraction-free Zen mode on every typing page.
- Align every arena mode radio consistently at the left edge of its selectable
  card and strengthen selected and keyboard-focus states.

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
