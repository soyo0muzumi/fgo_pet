# character Agent Rules

## Scope

Applies to this module tree and inherits the repository root `AGENTS.md`.

## Current boundary

The `FgoPet.Character` project contains Web role-package, personalization and theme settings pages. It references Kernel, Content and UiSdk, without legacy Core or Infrastructure. `CharacterSettingsRegistration` supplies settings metadata and Web command owners; the host owns window routing and placement.

- Kernel owns active role identity, profile/preferences, neutral semantic presentation, and process/context lifecycle.
- Content owns package validation/installation, persona and knowledge reads, event feedback, and the unique `RoleActivationService`.
- Static portrait provider owns snapshots, controller, and WPF rendering; Kernel exposes only the portrait backend/controller contracts.
- This module owns the role-package, personalization and theme Web pages.

## Safety invariants

Treat role packages as untrusted input. Keep integrity and version checks, and ensure archive extraction cannot escape its destination. Treat preferences/profile as user data.

## Validation

When changing this module, run the relevant package, portrait, and character UI checks. Package installation and validation require malicious-archive coverage.
