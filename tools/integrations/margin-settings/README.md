# Margin Settings frontend

Editable React sources for the `Theme` and `ModelConnection` modules hosted by the
existing Settings root. Adapted from the supplied Margin 0.4.0 migration bundle.
The browser-only preview pages, sample data, Chat resources and character artwork
are not part of this integration.

## Build and update

Requires Node.js 22.18+ and npm. From this directory:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm test
npm run build
npm run sync
```

`sync` copies the two ESM entry points into their existing Character and Dialogue
owners and copies shared JS, CSS, fonts, notices and a hash manifest into
`src/FgoPet.Desktop/Shell/Desktop/ui/settings/root/margin/`. These generated runtime
assets are intentional application inputs. `dist/` and `node_modules/` are ignored.
Build before syncing; do not edit generated entry points or bundles directly.
The inherited lockfile pins the supplied frontend toolchain; its broader preview
dependencies are not automatically included in the settings bundle.

## Runtime boundaries

Each entry exports `mount(container, context)` and returns cleanup. The existing
root owns navigation, page IDs, command results, timeouts and cancellation.
Controllers order draft acknowledgements before save and reject stale results.
Password fields are uncontrolled, write-only and reset after successful save,
clear or provider change; secrets never enter snapshots, route state or storage.
The existing clear-key operation remains available. Reasoning visibility saves
separately through its existing command.

Model lists are requested on entry, opening the picker and confirmed source
changes. This checkout's owner still implements explicit refresh without the
later handoff baseline's draft-catalog cache. The frontend coalesces concurrent
refreshes, does not select the first model and does not persist a connection.
Chat continues to consume its separate persisted `ConfiguredModels` catalog.
Unknown dirty state is not presented as a successful save.

Theme selection saves through the existing Theme owner after acknowledgement.
The page previews effective light/dark colors; the root, Chat and other pages keep
their existing palettes. Motion follows the system preference. Other settings
modules and native window behavior remain under their existing owners.

Fonts are local DM Sans and DM Serif Display under the included OFL licenses.
The settings CSP permits local scripts, CSS and fonts; it does not permit inline
scripts/styles or service connections. Third-party notices accompany the runtime.

## Verification

- `npm test`: input ordering, stale responses, failed saves, provider/key isolation,
  theme acknowledgement and page cancellation.
- App tests: `ModelConnectionViewModelTests`, `ModelConnectionWebPageTests`,
  `ConfiguredModelCatalogTests`.
- Windows tests: `MarginReactSettingsWebTests`, `ThemeWebTests`,
  `SettingsWebRootTests`, plus unchanged Speech checks in
  `MarginServiceSettingsWebTests`.

Windows tests use synthetic owners and never contact a provider or use real
credentials. Optional `FGO_PET_MARGIN_SERVICE_CAPTURE_DIR` and
`FGO_PET_MARGIN_CHARACTER_CAPTURE_DIR` capture fixture images and sanitized
boundary logs. User acceptance, real credentials, service connectivity and
multi-monitor DPI behavior require separate verification.
