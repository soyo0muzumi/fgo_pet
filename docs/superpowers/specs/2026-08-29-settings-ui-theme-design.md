# Settings UI and theme design

## Goal

Redesign the current WPF settings experience so servant management and model connection feel like one coherent desktop application. The result must improve hierarchy, readability, and discoverability without changing the existing business behavior.

The application will ship with two selectable themes:

- **Modern Gray** — the default. Neutral gray surfaces, restrained Windows-style blue accents, and low decorative density.
- **FGO Light** — optional. Navy navigation, muted gold accents, and restrained FGO-inspired details while retaining the same layout and control behavior.

The theme scope is limited to `ServantLibraryWindow` and `ModelConnectionWindow`. The portrait window, attached dialogue panel, and other runtime surfaces are not part of this change.

## Information architecture

`ServantLibraryWindow` becomes the primary settings shell. It uses a persistent left sidebar and a right content surface. The sidebar exposes these destinations:

1. 从者与外观
2. 称呼设置
3. 角色包管理
4. 模型连接
5. 记忆与数据
6. Theme / 主题

The servant list remains searchable and available within the servant-and-appearance destination rather than occupying half of every settings page. Existing selection, activation, package, address, and data-management actions remain available under their matching destination.

`ModelConnectionWindow` remains a usable independent window because the tray and startup flow already open it directly. It does not duplicate the theme picker. It follows the theme selected from the primary settings sidebar.

## Window layout

### Primary settings shell

The window uses three structural layers:

- A compact sidebar with the product label, navigation items, and clear selected state.
- A page header with the active destination title and one-line description.
- A scrollable content region composed of focused sections rather than one long vertical stack.

The servant-and-appearance page contains a searchable servant list, selected-servant summary, appearance selector, and the primary “设为当前从者” action. Package removal is visually separated from the primary action.

The address page groups package-default and custom-address modes, the custom text field, save action, and status feedback.

The package page groups the local package path, install action, pack-folder shortcut, rescan action, and diagnostics. Diagnostics retain their conditional visibility and use theme-aware warning resources.

The model page provides an entry to open or activate the independent model connection window rather than duplicating its editable form and state. `DesktopAppUi` supplies this navigation action so the settings view model does not own another window.

The memory-and-data page provides an entry to the existing memory window and clearly labels destructive data operations as a separate concern.

The theme page presents Modern Gray and FGO Light as two preview cards. Selecting a card applies and saves the theme immediately; there is no separate save button.

### Model connection window

The connection form is regrouped into:

1. Provider and credential
2. Endpoint
3. Model selection
4. Connection status and actions

The footer keeps “跳过，离线使用”, “测试连接”, and “保存” together with clear emphasis: save is primary, test is secondary, and offline use is low emphasis. Existing control names, command bindings, and password handling remain intact.

## Theme architecture

Theme implementation uses shared WPF resource dictionaries rather than window-local hard-coded colors.

- A theme identifier represents `ModernGray` and `FgoLight`.
- A theme service owns the active theme, swaps the color resource dictionary, and notifies interested windows.
- Shared component resources define reusable button, input, navigation, card, status, and typography styles using dynamic theme resources.
- Separate Modern Gray and FGO Light dictionaries provide only semantic color and decorative tokens.
- Both settings windows consume the same shared styles and dynamic resources.

Theme selection is stored as an optional identifier in `AppSettings`. Loading an absent, blank, or unrecognized value resolves to Modern Gray. This additive field keeps the JSON schema at version 2, and existing version 1 and version 2 settings remain readable without user migration.

## State and data flow

At application startup:

1. The settings store loads the saved theme identifier.
2. The theme service resolves it, falling back to Modern Gray when needed.
3. The application loads shared component resources and the resolved color dictionary.
4. Both settings windows render against the same active resources.

When the user selects a theme card:

1. The theme page sends the selected identifier to the theme service.
2. The service validates and swaps the active resource dictionary on the UI thread.
3. Open settings windows update immediately through dynamic resources.
4. The validated identifier is persisted to the existing settings store.

Theme changes do not recreate view models, reset form input, close windows, or alter the active servant.

## Error handling

- Missing or unknown theme settings fall back to Modern Gray.
- A theme resource load failure keeps the last successfully loaded theme; if no theme has loaded, the application uses a minimal Modern Gray fallback.
- Theme persistence failure must not crash or close the settings windows. The UI remains on the applied theme for the current process and the theme page shows a concise save-failure status message.
- Existing model connection, package diagnostic, and settings quarantine behavior remains unchanged.
- Destructive actions retain confirmation and warning treatment and are never styled as primary actions.

## Compatibility constraints

- Keep existing view-model commands and business operations unchanged unless a small navigation adapter is necessary.
- Preserve named controls used by `ModelConnectionWindowIntegrationTests`, including `ProviderComboBox`, `ApiKeyBox`, and `ModelTextBox`.
- Preserve the independent model window title and tray behavior.
- Do not introduce a third-party WPF theme framework for this scope.
- Do not restyle runtime portrait, dialogue, focus, timeline, or memory windows as part of this implementation.

## Testing and acceptance

Automated coverage will verify:

- Modern Gray is the default for new and legacy settings.
- Theme choice round-trips through `JsonAppSettingsStore`.
- Unknown theme values fall back safely.
- Selecting a theme applies it and persists it.
- The sidebar exposes `Theme / 主题` as a navigation destination.
- Both settings windows resolve shared theme resources.
- Existing model connection controls, commands, and window behavior remain available.
- Existing servant, package, address, and model tests continue to pass.

Manual visual verification will open both windows in both themes and check:

- No clipped labels or controls at minimum supported window sizes.
- Sidebar and content remain usable at normal Windows display scaling.
- Selected, focused, disabled, warning, error, and destructive states remain distinguishable.
- Text and interactive controls maintain readable contrast.
- Switching themes does not lose unsaved model form input or servant selection.

The first implementation targets a coherent, stable visual baseline. Fine-grained color, spacing, and ornamental adjustments may be refined later without changing the resource or navigation architecture.
