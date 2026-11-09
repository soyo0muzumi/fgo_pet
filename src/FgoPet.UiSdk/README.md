# UI SDK

`ExternalImagePreview.IsAllowedUrl` exposes the existing public-HTTPS syntax/address preflight for bounded native consumers; it grants no permission. The transport still validates DNS addresses when connecting, refuses redirects/cookies and returns only decoded, metadata-free raster pixels. Dialogue's separately authorized collector reuses these pixels at its own persistent-storage boundary.

`WebView2SurfaceHost` supports an optional host-owned `web.localImage` loader alongside external previews. The same exact-document origin policy, bounded request IDs, shared concurrency limit and PNG data-URL bound apply. UI SDK validates the transport; the feature callback owns asset authorization, path validation and decoding. No file URL or arbitrary filesystem path is accepted by this transport.

`MarginVisualResources`, `MarginStrings` and `Resources/Web/` own the frozen Margin v5 paper palette, typography roles, native entry strings and generic select presentation. Chat and Settings consume these through the existing local asset and theme-event paths. Margin uses the frozen light paper palette; other surfaces retain their selected ThemeService palette. The select adapter leaves values, validation and change commands with the page owner.

`Resources/Shell/MarginContextMenu.xaml` supplies explicit native context menu/action styles, including paper hover and keyboard focus. Apply action styles to MenuItem instances; separators have a distinct template. Generic dropdown Escape is consumed by the picker so it does not dismiss the containing feature surface.

WPF workspace, settings, compact and portrait content contracts, shared controls and theme services. Owners construct content lazily; the shell owns windows, navigation, placement and container lifetime. Factories capture feature dependencies at composition and never receive the root service provider.

`ICompactSurface` supplies generic activity and auto-collapse blocking; the content receives expansion through `ICompactSurfaceView`. The Focus owner implements its timer and editing inside that content. Portrait hosting consumes an immutable `IPortraitFrame` from `IPortraitSurface`, so geometry, rendering and hit testing use one published renderer state without exposing bitmap snapshots or native SDK details to the shell.

The consumed legacy `ISettingsNavigator` and `PackageDetailRoute` publish presentation navigation here with their existing namespaces and command signatures; they do not require a host assembly. The compatibility SettingsSection enum compiles in Platform.Contracts with unchanged values.

`IUserDataExporter`/`IUserDataDeleter` publish the existing UI privacy request ports here. DataManagement owns their policy and execution; confirmation remains in the existing privacy page. These ports expose no repositories and preserve their signatures.

`ThemeService` compiles here. Persisted `ThemeSettings` and `AppTheme` compile in Platform.Contracts. Existing namespaces and App theme resource URIs are preserved. `design/tokens.json` supplies both WPF theme color dictionaries and Web fallback CSS; run `python tools/scripts/generate_theme_tokens.py --check` to detect drift. System theme mode follows the Windows app color preference, while explicit light/dark selections remain fixed. The retired UiFoundation project is not a second compilation owner.

Transient surfaces are registered by ID through `ITransientSurfaceViewFactory`; owners still return a `FrameworkElement`. `WebView2SurfaceHost` restricts local navigation and accepts only declared typed commands. The shell owns the separate transient window and its lifetime, and no feature receives a generic host-object bridge.

External presentation is opt-in per Web surface. Links require a trusted user click and a validated HTTP(S) address, then open through the host callback; navigation inside WebView remains restricted to the local origin. Image previews use a separate bounded native callback and correlated `web.imagePreview`/`web.imagePreview.result` envelopes from that exact origin. `ExternalImagePreview` accepts public HTTPS endpoints on the default port, validates and pins resolved addresses, rejects redirects, cookies and proxies, and limits the full download to 12 seconds/4 MiB. Only single-frame PNG/JPEG up to 4096 per dimension and 4 Mi pixels are decoded and re-encoded as metadata-free PNG. No remote image URL is assigned to WebView and no pixels are saved to disk. Surface closure cancels pending loads; diagnostics contain safe codes only.

`ISettingsWebPage` contributes a local Settings page module and named commands with catalog metadata supplied through the existing factory contract. Built-in page factories carry metadata only and reject legacy CreateView calls with SETTINGS_USE_WEB_ROOT; the Settings window uses Web modules. The published factory contract remains available to external content consumers. Mutable owners create an independent handler per root host. Asynchronous handlers must honor the supplied cancellation token before committing; navigation and host closure cancel pending page work. Catalog metadata and feature state remain with their existing owners.

Dependencies: Extensibility, Platform.Contracts and published Kernel geometry/portrait contracts, plus WPF/framework libraries and the existing CommunityToolkit command contract. No feature implementation or desktop-host dependency.

Validation: shared control/theme, generic content replacement, portrait/window placement and disposal cases in Windows.Tests; settings coordinator cases in SettingsHost.Tests; the evaluated architecture gate.
