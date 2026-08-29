# Settings UI and theme design

> Revision 2026-08-29: model connection and role-package management are settings
> destinations. Neither is a tray destination or an independent user-facing login
> window.

## Goal

Redesign the current WPF settings experience so global user settings, personalization,
model connection, conversation controls, and data privacy form one coherent desktop
application. Role-package management is a settings destination because it owns
package installation, appearance activation, preview, and servant-scoped
preferences. The result must improve hierarchy, readability, and discoverability
without changing the existing business behavior.

The application will ship with two selectable themes:

- **Modern Gray** — the default. Neutral gray surfaces, restrained Windows-style blue accents, and low decorative density.
- **FGO Light** — optional. Navy navigation, muted gold accents, and restrained FGO-inspired details while retaining the same layout and control behavior.

The theme scope covers `SettingsWindow`. The portrait window, attached dialogue
panel, and other runtime surfaces are not part of this settings redesign.

## Information architecture

`SettingsWindow` becomes the primary global settings shell. It uses a persistent left
sidebar and a right content surface. The sidebar exposes these destinations:

1. 用户资料
2. 个性化
3. 角色包
4. AI 模型与连接
5. 对话与记忆
6. 数据与隐私
7. 主题

The `角色包` destination contains the installed package list and the package detail
route. It replaces the separate servant-library window. The package list handles
installation, scanning, and package-level diagnostics. Selecting `打开角色包`
enters the detail route for one package and its servant.

The package detail route contains `从者与外观`, `称呼设置`, preview,
activation, version/source metadata, and package-defined declarative settings.
Address preferences remain owned by the selected servant and are saved by
`servant_id`.

The tray and portrait context menu expose only `设置` for configuration. They do
not expose `模型连接` or `从者库` as separate top-level destinations. Selecting
`设置` opens the global settings shell. The AI model page is the only user-facing
entry for provider, credential, endpoint, and model configuration.

### Navigation iconography

Every settings destination and package-detail subsection uses a consistent 16–18 px linear
vector icon placed before its text label. Icons use semantic theme resources rather
than hard-coded colors: muted in the idle state, accent-colored in the selected
state, and visibly disabled when the destination is unavailable. The icon is
supporting navigation information, not a replacement for the text label.

| Destination | Icon concept |
| --- | --- |
| 用户资料 | person / profile |
| 个性化 | sliders / adjust |
| 角色包 | archive / package |
| AI 模型与连接 | plug / connection |
| 对话与记忆 | chat bubble |
| 数据与隐私 | shield / lock |
| Theme / 主题 | palette |
| 从者与外观 | person with star |
| 称呼设置 | speech bubble |
| 角色包信息 | info / document |

The icon set is bundled or rendered from application-owned vector path resources so
it does not depend on emoji rendering or an optional third-party font. Icons must
retain a readable silhouette in both Modern Gray and FGO Light themes, at normal
Windows display scaling, and with keyboard focus. Tooltips and automation names
retain the full destination label for accessibility.

## Window layout

### Primary settings shell

The window uses three structural layers:

- A compact sidebar with the product label, navigation items, and clear selected state.
- A page header with the active destination title and one-line description.
- A scrollable content region composed of focused sections rather than one long vertical stack.

The role-package page contains searchable installed-package cards. Each card shows
the package preview, display name, package ID, version, source badge, and active
state. The page groups the local `.fgopetpack` file picker, install action, pack-folder
shortcut, rescan action, and diagnostics. Diagnostics retain their conditional
visibility and use theme-aware warning resources.

Selecting `打开角色包` enters a package detail route with a header, preview area,
version/source/compatibility metadata, and an internal navigation row for `从者与
外观`, `称呼设置`, and `角色包信息`. The appearance page contains the appearance
selector and the primary “设为当前从者” action. Package removal is visually
separated from the primary action. The address page contains package-default and
user-defined modes, the custom text field, save action, and status feedback.

Package-defined settings are declarative and limited to application-approved field
types. A package cannot inject arbitrary WPF controls, executable behavior, or
override global privacy, security, or provider settings.

The model page contains the provider, credential, endpoint, model selection, test,
save, clear-key, and offline actions. It does not contain role-package selectors,
appearance selectors, user profile fields, or servant address controls.

The conversation-and-memory page provides an entry to the existing memory window and
clearly labels destructive data operations as a separate concern. The data-and-
privacy page owns export, credential clearing, and all-data deletion actions.

The theme page presents Modern Gray and FGO Light as two preview cards. Selecting a card applies and saves the theme immediately; there is no separate save button.

### AI model and connection page

The connection form is regrouped into:

1. Provider and credential
2. Endpoint
3. Model selection
4. Connection status and actions

The footer keeps “跳过，离线使用”, “测试连接”, and “保存” together with clear emphasis: save is primary, test is secondary, and offline use is low emphasis. Existing control names, command bindings, and password handling remain intact even though the form is hosted inside the global settings shell.

### User profile and servant address boundary

The user-profile page may show and edit global profile data such as an optional
display name. This value is not automatically used as every servant's address.
Each servant continues to expose exactly two address modes in its package detail:
package default or user-defined. A user-defined address is stored under that
servant's stable `servant_id` and remains independent of appearance changes.

Package upgrades may change Persona, Knowledge, available appearances, and the
package-defined settings schema. Runtime content resolves by package ID, package
version, servant ID, and appearance ID as appropriate. User-owned address and
memory data remains keyed by `servant_id`; package upgrades must not silently move
or delete it. When a package-defined setting becomes invalid, the application
revalidates it against the new schema, falls back to its declared default, and
shows a non-blocking migration notice.

## Theme architecture

Theme implementation uses shared WPF resource dictionaries rather than window-local hard-coded colors.

- A theme identifier represents `ModernGray` and `FgoLight`.
- A theme service owns the active theme, swaps the color resource dictionary, and notifies interested windows.
- Shared component resources define reusable button, input, navigation, card, status, and typography styles using dynamic theme resources.
- Separate Modern Gray and FGO Light dictionaries provide only semantic color and decorative tokens.
- The settings shell and package detail routes consume the same shared styles and
  dynamic resources.

Theme selection is stored as an optional identifier in `AppSettings`. Loading an absent, blank, or unrecognized value resolves to Modern Gray. This additive field keeps the JSON schema at version 2, and existing version 1 and version 2 settings remain readable without user migration.

## State and data flow

At application startup:

1. The settings store loads the saved theme identifier.
2. The theme service resolves it, falling back to Modern Gray when needed.
3. The application loads shared component resources and the resolved color dictionary.
4. The settings shell and package detail routes render against the same active
   resources.

When the user selects a theme card:

1. The theme page sends the selected identifier to the theme service.
2. The service validates and swaps the active resource dictionary on the UI thread.
3. Open settings windows update immediately through dynamic resources.
4. The validated identifier is persisted to the existing settings store.

Theme changes do not recreate view models, reset form input, close windows, or alter the active servant.

On first launch, the application does not force a model login. If no role package is
installed, startup opens `设置 > 角色包` so package loading remains a separate flow.
If a servant is available but model connection is absent, the portrait remains
usable offline; the dialogue surface provides a `去设置` action that navigates to
`设置 > AI 模型与连接`.

## Error handling

- Missing or unknown theme settings fall back to Modern Gray.
- A theme resource load failure keeps the last successfully loaded theme; if no theme has loaded, the application uses a minimal Modern Gray fallback.
- Theme persistence failure must not crash or close the settings windows. The UI remains on the applied theme for the current process and the theme page shows a concise save-failure status message.
- Existing model connection, package diagnostic, and settings quarantine behavior remains unchanged.
- Destructive actions retain confirmation and warning treatment and are never styled as primary actions.

## Compatibility constraints

- Keep existing view-model commands and business operations unchanged unless a small navigation adapter is necessary.
- Preserve named controls used by `ModelConnectionWindowIntegrationTests`, including `ProviderComboBox`, `ApiKeyBox`, and `ModelTextBox`.
- Do not preserve a separate model-connection or servant-library tray item or
  portrait-menu item. Both menus expose the global `设置` entry instead.
- Do not introduce a third-party WPF theme framework for this scope.
- Do not restyle runtime portrait, dialogue, focus, timeline, or memory windows as part of this implementation.

## Testing and acceptance

Automated coverage will verify:

- Modern Gray is the default for new and legacy settings.
- Theme choice round-trips through `JsonAppSettingsStore`.
- Unknown theme values fall back safely.
- Selecting a theme applies it and persists it.
- The sidebar exposes `主题` as a navigation destination.
- The settings shell and package detail routes resolve shared theme resources.
- Every settings destination displays the correct bundled vector icon with distinct
  idle, selected, focused, and disabled states.
- Existing model connection controls and commands remain available on the AI model
  page.
- Tray and portrait menus contain no direct `模型连接` item; the settings entry
  opens the AI model page.
- Existing servant, package, address, and model tests continue to pass.

Manual visual verification will open the settings shell and package detail route in
both themes and check:

- No clipped labels or controls at minimum supported window sizes.
- Sidebar and content remain usable at normal Windows display scaling.
- Selected, focused, disabled, warning, error, and destructive states remain distinguishable.
- Text and interactive controls maintain readable contrast.
- Switching themes does not lose unsaved model form input or servant selection.
- A clean user profile can discover package installation and model configuration
  through separate actions without requiring either one to be configured.

The first implementation targets a coherent, stable visual baseline. Fine-grained color, spacing, and ornamental adjustments may be refined later without changing the resource or navigation architecture.
