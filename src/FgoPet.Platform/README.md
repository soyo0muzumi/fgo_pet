# platform

Platform provides business-neutral contracts and shared persistence primitives.

## Responsibilities

- `FgoPet.Platform.Contracts` owns cross-cutting value types and interfaces, including runtime event, settings document, geometry, and window placement contracts.
- `FgoPet.Platform.Storage` owns SQLite runtime database access and migrations, atomic JSON storage, settings documents, and JSON window-placement persistence.
- `RuntimeDatabase` uses private-cache connections with WAL, foreign keys and a bounded busy timeout. Connections that fail during initialization are disposed before the error reaches the caller.
- `RuntimeDatabaseMigrator.MigrateModule` applies ordered module-owned scripts and version records in one transaction, independently of the platform schema version. Business schemas stay with their feature owners.
- `FgoPet.Platform.Windows` owns Windows Credential Manager, monitor-layout, protected-state and explicitly selected-window text implementations. UI Automation uses the Windows desktop framework; user-facing windows remain in DesktopShell. The compatibility credential interfaces keep their namespaces in Platform.Contracts. `IProtectedStateProtector` is the business-neutral protected-state port.

`IWindowTextReader` lists bounded native candidates and reads one opaque selected candidate on demand. Windows rechecks process identity and scans metadata before reading content. Document/Edit TextPattern bodies are read only after a complete safe subtree scan; a nonempty body replaces descendant output to avoid duplication. Password, off-screen content, unauthorized process nodes, provider failures and incomplete scans block ancestor aggregation; safe descendants remain eligible. Fully inspected ScrollBar branches consisting only of non-password, authorized ScrollBar/Button/Thumb controls without TextPattern or ValuePattern are non-text decorations, including hidden thumb/page buttons, and never contribute output. Safety metadata and observed child identity/order are rechecked before output; changed branches are rejected. UIA is live rather than an atomic snapshot, and compatibility depends on truthful provider metadata. ValuePattern and Edit/Document name fallbacks are not used.

`WindowTextNativeScope` additionally admits renderer-process nodes only when a bounded RawView parent walk reaches their nearest native HWND with a matching process ID, and that HWND is a native child of the exact selected window. It never skips an unrelated nearest HWND to find a more permissive ancestor. Root HWND/process identity, child ownership and process start times are rechecked; another window or a reused PID cannot inherit authorization. A process name or process family alone grants no authority.

Before UIA traversal, `WindowTextReadRoot` inspects at most 400 native descendants of the selected window. A unique visible `Chrome_RenderWidgetHostHWND` child becomes the content root, avoiding browser chrome that can exhaust the reading deadline before reaching the document. No match, multiple matches or incomplete enumeration uses the original selected root instead of guessing. The selected top-level window remains the authority: the viewport's visibility, native parent relationship, process ID and process start time are rechecked throughout collection, including for same-process content. The content-root selection must still match after reading. A class name alone never grants access, and this is neither a tab enumeration API nor a background browser reader.

Limits remain 6,000 characters, 400 scanned nodes and depth 32. A three-second caller deadline suppresses late results; synchronous UI Automation providers cannot be forcibly interrupted. One process-wide worker prevents accumulating blocked reads. No screenshot, OCR or background monitoring is performed. The caller owns consent and request lifetime; the current host has no window-text trigger UI. `Complete` means traversal completed under this policy, not that every document or provider is supported. Elevated, inaccessible, virtualized or unanchored cross-process content may be unavailable; no automatic elevation or permission change is attempted.

## Boundaries

Platform contracts and storage stay free of feature rules and business-module dependencies. Feature repositories and business schemas remain with their owning modules. Do not move user-facing UI or business settings defaults into this layer.

## Security

Credential implementations must keep secrets protected by Windows credential storage/DPAPI. `WindowsStateProtector` uses DPAPI `CurrentUser`, purpose-separated entropy and bounded envelopes; it has no plaintext fallback. Database migrations must be safe to retry and leave the database consistent on failure. Diagnostics must not include user data.

## Validation

`IBoundedProcessRunner` is a neutral process-supervision port. Windows implements suspended process creation, explicit inherited standard streams, a Job with child-process, memory and lifetime limits, bounded capture and confirmed termination. Safe diagnostics are asynchronous and bounded; output and stdin remain private owner data. These limits do not reduce the host user's filesystem or network permissions and are not a sandbox.

Use `tests/FgoPet.Infrastructure.Tests` for database, SQLite, settings-document, and storage behavior. Use `tests/FgoPet.Windows.Tests` for Windows-specific implementations, including `WindowsStateProtectorTests`. Migration behavior must be covered for both empty and already-migrated databases.
