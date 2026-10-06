# Focus

Focus.Core (`plugins/FgoPet.Plugin.Focus/Core`) owns the timer state machine, presets, session recovery, completion ledger, bond progression and timeline persistence. Session snapshots are presentation inputs; only committed transitions create business facts and companion signals.

Focus.Desktop owns the process cadence and the compact Focus view. `FocusCompactViewModel` owns preset editing, validation, countdown formatting, progress and timer commands. `FocusRegistration.AddFocusCapability` publishes the lazy `ICompactSurface` content through UiSdk; the desktop shell supplies container expansion and placement. Removing the capability removes the view and timer registration while keeping stored data available to privacy/backup adapters.

The desktop projection observes the Kernel's current role, detaches on disposal and fences view construction and commands during process shutdown. Ticks update content without requesting shell geometry changes unless activity visibility changes. Loading or hiding a view does not create a second timer.

Dependencies are owned Focus.Core, neutral Platform storage/contracts, Extensibility signals, Kernel role/lifetime contracts and UiSdk. Focus must not reference Dialogue or DesktopShell implementation.

Owned tables: `focus_presets`, `focus_sessions`, `runtime_events`, `timeline_entries`, `servant_bonds`, `bond_ledger`. Recovery must preserve exactly-once completion accounting; display snapshots must not be written as authoritative facts.

Native `focus.get/start/pause/stop` adapters use the existing session service through `IFocusNativeDispatcher`. Desktop supplies one WPF dispatcher and cadence owner, and checks the active role again at dispatch. Get is read-only; mutations require native Command authorization, which production currently denies pending the approval UI. The adapter introduces no second timer or completion ledger.

Validation uses Core focus/bond/timeline tests, Infrastructure ledger/recovery tests, App compact/Focus tests and Windows focus/panel/window/theme integration tests. Release compilation checks the actual assembly boundary. Device and sleep/resume acceptance are separate from automated checks.
