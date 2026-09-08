# ui-foundation

## Responsibilities

Own reusable WPF visual foundations: theme tokens, colors, typography, spacing, common controls, icons, converters, animation conventions, and accessibility constraints.

## Non-responsibilities

Do not own Focus, Todo, Dialogue, memory, package, Agent, or desktop-shell business state, feature views, feature view models, or feature-specific styles.

## Current source and tests

Candidate current source is in `src/FgoPet.App/{Themes,Theming}`, `SettingsControls.xaml`, shared icons, and business-neutral controls, with corresponding theme, resource-loading, and common-control tests.

## Target layout

Establish the directory and ownership boundary first. A separate `FgoPet.UiFoundation.csproj` is not required in the first phase.

## Public interfaces

Theme resources, visual tokens, common controls, converters, icon resources, animation conventions, and accessibility guidance.

## Dependencies

May depend on framework/WPF and `foundation` primitives. It must not depend on `desktop-shell` or any feature module.

## Data and security boundaries

Keep visual resources free of business state, secrets, prompts, user content, and local machine data. Accessibility and safe resource loading are part of the boundary.

## Development and validation

Run theme, resource-loading, common-control, and affected Windows tests, followed by the release build for shared resource changes.

## Migration status

Documentation skeleton only. Candidate resources and tests remain in the legacy App and Windows test projects.

## Migration debt

Feature-specific styles and controls must be separated from reusable theme resources before any assembly split.
