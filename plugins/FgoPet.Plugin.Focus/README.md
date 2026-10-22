# Focus

Focus.Core (`plugins/FgoPet.Plugin.Focus/Core`) owns the timer state machine, presets, session recovery, completion ledger, bond progression and timeline persistence. Session snapshots are presentation inputs; only committed transitions create business facts and companion signals.

Focus.Desktop owns the process cadence and the compact Focus view. `FocusCompactViewModel` owns preset editing, validation, countdown formatting, progress and timer commands. `FocusRegistration.AddFocusCapability` publishes the lazy `ICompactSurface` content through UiSdk; the desktop shell supplies container expansion and placement. Removing the capability removes the view and timer registration while keeping stored data available to privacy/backup adapters.

The desktop projection observes the Kernel's current role, detaches on disposal and fences view construction and commands during process shutdown. Ticks update content without requesting shell geometry changes unless activity visibility changes. Loading or hiding a view does not create a second timer.

The shell's Focus menu toggles the expanded panel. The inactive setup card has a Close panel button; closing it or pressing Escape preserves its options and does not start or stop a session. Running details can be dismissed independently of the timer; Stop remains the explicit session-ending command.

Margin presents 10/25/50-minute quick starts as single-cycle sessions. Session options retain the existing 5–180 minute duration, 1–60 minute break and 1–12 cycle bounds, with a 25/5/1 initial selection. An active session shows a three-pixel remaining-progress ribbon; hovering or clicking reveals remaining time, pause/resume and stop. Clicking outside the running controls or deactivating their owner window dismisses the details without stopping the session; a second ribbon click also dismisses them while the pointer remains over the view. English presentation labels belong to `FocusStrings`. The existing preset catalog, cadence, recovery and completion owner remain authoritative.

Dependencies are owned Focus.Core, neutral Platform storage/contracts, Extensibility signals, Kernel role/lifetime contracts and UiSdk. Focus must not reference Dialogue or DesktopShell implementation.

Owned tables: `focus_presets`, `focus_sessions`, `runtime_events`, `timeline_entries`, `servant_bonds`, `bond_ledger`. Recovery must preserve exactly-once completion accounting; display snapshots must not be written as authoritative facts.

Native `focus.get/start/pause/resume/stop` adapters use the existing session service through `IFocusNativeDispatcher`. Desktop supplies one WPF dispatcher and cadence owner, and checks the active role again at dispatch. Get is read-only. Production native policy allows only `firstparty.focus`'s `focus.resume` to request explicit per-call user approval; native start, pause and stop remain denied, without changing the existing desktop timer controls. Resume requires the expected paused session and preserves its phase, remaining time and cycle; retries with the same idempotency key return the original receipt, including an uncertain result after persistence failure. Resource authorization binds the paused timestamp and budget so an intervening resume/pause invalidates the earlier approval, while running countdown updates do not invalidate authorization. The adapter introduces no second timer or completion ledger.

Validation uses Core focus/bond/timeline tests, Infrastructure ledger/recovery tests, App compact/Focus tests and Windows focus/panel/window/theme integration tests. Release compilation checks the actual assembly boundary. Device and sleep/resume acceptance are separate from automated checks.
