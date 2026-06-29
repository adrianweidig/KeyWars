# Third-Party Notices

KeyWars is designed for self-hosted operation. Browser runtime assets are
vendored locally under `src/KeyWars/wwwroot` and must not depend on CDNs or
external services.

## Visual Asset Manifest

The authoritative inventory for downloaded visual source packages, SHA256
hashes, licenses, and generated runtime derivatives is:

- `third_party/visual-assets/asset-manifest.json`

Original archives and official-page downloads are kept outside `wwwroot` under
`third_party/visual-assets/originals/`. Optimized runtime derivatives are kept
under `src/KeyWars/wwwroot/vendor/keywars-assets/`.

## Bootstrap Icons

- Package: `bootstrap-icons` v1.13.1
- Source: https://icons.getbootstrap.com/
- Source archive: `third_party/visual-assets/originals/bootstrap-icons-1.13.1.tgz`
- License file: `third_party/visual-assets/licenses/bootstrap-icons.txt`
- Runtime use: selected symbols in
  `src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg`
- License: MIT

## Heroicons

- Package: `heroicons` v2.2.0
- Source: https://heroicons.com/
- Source archive: `third_party/visual-assets/originals/heroicons-2.2.0.tgz`
- License file: `third_party/visual-assets/licenses/heroicons.txt`
- Runtime use: selected symbols in
  `src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg`
- License: MIT

## Tabler Icons

- Package: `@tabler/icons` v3.44.0
- Source: https://tabler.io/icons
- Source archive: `third_party/visual-assets/originals/tabler-icons-3.44.0.tgz`
- License file: `third_party/visual-assets/licenses/tabler-icons.txt`
- Runtime use: selected symbols in
  `src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg`
- License: MIT

## Lucide Static

- Package: `lucide-static` v1.22.0
- Source: https://lucide.dev/
- Source archive: `third_party/visual-assets/originals/lucide-static-1.22.0.tgz`
- License file: `third_party/visual-assets/licenses/lucide-static.txt`
- Runtime use: selected symbols in
  `src/KeyWars/wwwroot/vendor/keywars-assets/keywars-icons.svg`
- License: ISC

## Open Peeps

- Source: https://www.openpeeps.com/
- Source page snapshot:
  `third_party/visual-assets/originals/open-peeps/open-peeps.html`
- Downloaded official-page assets:
  `third_party/visual-assets/originals/open-peeps/assets/`
- License note: `third_party/visual-assets/licenses/open-peeps.txt`
- Runtime use: selected/fallback motivational illustrations in
  `src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/`
- License: CC0, as described by the official project page.
- Automation note: direct official-page SVG/PNG assets are vendored. Full
  library downloads that require interactive checkout are not automated.

## Humaaans

- Source: https://www.humaaans.com/
- Source page snapshot:
  `third_party/visual-assets/originals/humaaans/humaaans.html`
- Downloaded official-page assets:
  `third_party/visual-assets/originals/humaaans/assets/`
- License note: `third_party/visual-assets/licenses/humaaans.txt`
- Runtime use: selected/fallback motivational illustrations in
  `src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/`
- License: CC0, as described by the official project page.
- Automation note: direct official-page SVG/PNG assets are vendored. Full
  library downloads that require interactive checkout are not automated.

## ASP.NET Core SignalR JavaScript Client

- Package: `@microsoft/signalr` v10.0.0
- Source package: https://www.npmjs.com/package/@microsoft/signalr
- Repository: https://github.com/dotnet/aspnetcore
- Local files: `src/KeyWars/wwwroot/vendor/signalr/signalr.min.js` and
  `src/KeyWars/wwwroot/vendor/signalr/signalr.min.js.map`
- Use in KeyWars: SignalR browser client for live arena communication.
- License: MIT, as declared by the npm package metadata.

## Explicitly Not Vendored

The following sources are intentionally not bulk-vendored:

- unDraw: https://undraw.co/license
- ManyPixels Gallery: https://www.manypixels.co/gallery

Their terms are not a clean fit for redistributing a local asset-bank package in
this repository. No individual unDraw or ManyPixels asset is used unless a
future change documents a separate source and license decision.

## Project-Owned Visual Assets

The following SVG/PNG assets were created specifically for KeyWars in this
repository and are covered by the repository license, not a third-party license:

- `src/KeyWars/wwwroot/img/keywars-circuit.svg`
- `src/KeyWars/wwwroot/img/favicon.svg`
- `src/KeyWars/wwwroot/img/apple-touch-icon.png`
- `src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/keywars-badge.svg`
- `src/KeyWars/wwwroot/vendor/keywars-assets/illustrations/reward-burst.svg`
