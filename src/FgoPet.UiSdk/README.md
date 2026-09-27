# UI SDK

WPF workspace, settings, compact and portrait content contracts, shared controls and theme services. Owners construct content lazily; the shell owns windows, navigation, placement and container lifetime. Factories capture feature dependencies at composition and never receive the root service provider.

`ICompactSurface` supplies generic activity and auto-collapse blocking; the content receives expansion through `ICompactSurfaceView`. The Focus owner implements its timer and editing inside that content. Portrait hosting consumes an immutable `IPortraitFrame` from `IPortraitSurface`, so geometry, rendering and hit testing use one published renderer state without exposing bitmap snapshots or native SDK details to the shell.

The consumed legacy `ISettingsNavigator` and `PackageDetailRoute` publish presentation navigation here with their existing namespaces and command signatures; they do not require a host assembly. The compatibility SettingsSection enum compiles in Platform.Contracts with unchanged values.

`IUserDataExporter`/`IUserDataDeleter` publish the existing UI privacy request ports here. DataManagement owns their policy and execution; confirmation remains in the existing privacy page. These ports expose no repositories and preserve their signatures.

`ThemeService` compiles here. Persisted `ThemeSettings` and `AppTheme` compile in Platform.Contracts. Existing namespaces and App theme resource URIs are preserved. The retired UiFoundation project is not a second compilation owner.

Dependencies: Extensibility, Platform.Contracts and published Kernel geometry/portrait contracts, plus WPF/framework libraries and the existing CommunityToolkit command contract. No feature implementation or desktop-host dependency.

Validation: shared control/theme, generic content replacement, portrait/window placement and disposal cases in Windows.Tests; settings coordinator cases in SettingsHost.Tests; the evaluated architecture gate.
