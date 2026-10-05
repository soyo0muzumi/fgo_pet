# Live2D portrait provider

Owns the validated Mash runtime session, transparent WebView2 portrait, alpha hit mask,
and procedural desktop gaze. The static portrait remains the fallback. Role packs
supply data only; SDK and behavior code belong to the application runtime.

`Live2DPortraitView` samples the global cursor at approximately 30 Hz while the live
surface is shown. Physical screen bounds are recalculated for every sample, including
window movement, scaling and DPI. The private `live2d.pointer` message contains viewport
relative X/Y and cursor activity; coordinates are not persisted or logged. Sampling
stops when the surface is released, unloaded or falls back to Static.

`Runtime/desktop-behavior.mjs` is bundled by `tools/scripts/prepare_live2d_runtime.py`.
Rebuild that runtime after changing behavior code, and supply the generated directory
with `-p:FgoPetLive2DRuntime=<runtime>` when building the app. Editing the source alone
does not update a previously generated runtime.

The gaze origin uses neutral Mash eye meshes (`Part5`/`Part6`, including descendants),
projected through the model's rendering matrix. Missing eye geometry disables gaze.
Eyes respond sooner than the head, with dead zones and bounded exponential smoothing.
After four seconds without cursor activity, attention softens; after eight seconds it
returns to small random gaze targets spaced 6–18 seconds apart. Entering face proximity
or tapping restores attention. Stationary hover does not hold attention indefinitely.
Body tracking is disabled.

Head hover uses current head-part triangles and atlas alpha samples, rejecting the
transparent portions of large face meshes. A 0.6-second dwell triggers one small
three-degree tilt plus a blink; the reaction lasts 0.7 seconds and has a randomized
5–8-second cooldown. Leaving releases the tilt over approximately 0.4 seconds.
Continuous hover triggers once until the pointer leaves. Tap cancels hover and holds
it suppressed until the pointer leaves/re-enters. Missing texture masks disable hover.
The atlas alpha maps are bounded to 1024 pixels per side and live only in the session.

The host viewport is 531×675 source pixels: 24 extra pixels on each side and 72 above
the original 483×603 area. Rendering retains the original character scale and bottom
alignment. Static fallback and its hit testing use the same bottom alignment.

`Live2DPresentationPlugin` projects the existing active-role presentation and chat
updates onto private renderer messages. It resolves the chat orchestrator at plugin
start, after the plugin catalog exists. Queued updates recheck active-role identity;
role replacement and plugin disposal reject stale work. Thinking starts when the user
message is persisted and ends when the answer streams, completes, fails or is cancelled.

`Runtime/desktop-state.mjs` owns Thinking → Sleep → semantic expression priority.
Named motions play once. `Thinking` and `Sleep` hold their final parameter pose until
the state changes; `Shy`, `Concerned` and `Surprised` smoothly return to default
parameters after completion (approximately 0.4-second recovery time constant). Sad uses Concerned; Happy and
Excited use procedural smiles. Gaze and hover yield to these motion states. After
ten minutes without cursor activity or pet/chat interaction, Sleep enters and holds
closed eyes. Cursor movement alone does not wake it. The first pet click wakes and
releases the pose without playing TapBody; a chat request also wakes it. Packs without
these optional motion groups retain their static/procedural fallback.

`Runtime/desktop-speech.mjs` consumes private `live2d.speech` energy messages and
owns only `ParamMouthOpenY`, after the SDK lip-sync updater. It limits opening to
0.55 with smooth attack/release, closes in silence and releases to the expression
baseline on stop or after 0.5 seconds without a playback frame. Actual playback wakes
Sleep and keeps the inactivity timer fresh; synthesis preparation does not. Missing
mouth parameters disable the effect. The host captures the role at playback start
and rejects old-session, switched-role and disposed-consumer frames.

Parameter order is motion base → blink → expression/state → gaze → breath → physics → lip sync
→ pose. The motion base returns smoothly to model defaults after a motion ends. Gaze
overrides eyeball X/Y and adds small head X/Y offsets; it yields while a motion plays,
then fades back in. Blink/mouth/body/hair parameters are not owned by gaze. Quiet breath
offsets replace the sample's strong head sway. Speech audio and energy extraction
remain owned by Speech; the renderer receives no audio or text. Hover blink is multiplied
into the SDK blink result in the same updater, before expressions; it does not reopen
closed eyes and yields to motion playback.

Validation:

```powershell
node --test tests/live2d_behavior.test.mjs tests/live2d_states.test.mjs tests/live2d_speech.test.mjs
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj -c Release --filter 'FullyQualifiedName~DesktopPointerSamplerTests|FullyQualifiedName~Live2DProviderTests'
node tests/live2d_runtime_probe.cjs <runtime> <staged-model-dir> <playwright-module> <ignored-output-dir>
```

The runtime probe checks actual SDK rendering, left/right parameter response, idle
attention, head-hover/alpha rejection, upward-looking headroom and Tap ownership/recovery using a locally supplied model with a TapBody
motion. With named state motions it also verifies held Thinking, blush and its gradual recovery, idle sleep,
closed eyes, click wake, procedural smile and speech sound/silence/stop/stale-frame
handling. Its hooks exist only in the served test copy. Automated tests do not establish
physical multi-monitor DPI, click-through or perceived naturalness acceptance.
