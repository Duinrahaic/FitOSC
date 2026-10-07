# Cross-platform migration

FitOSC keeps C# and moves to .NET 10 with separate Core and platform projects.
The desktop retains its Razor UI and three.js display. The planned desktop shell
is PhotinoX.Blazor, with a separate Skia SteamVR HUD and interactive wrist panel.
Linux packaging uses AppImage.

## Phase status and gates

| Phase | Scope | Acceptance gate | Status |
| --- | --- | --- | --- |
| 0 | UI, overlay and Linux BLE risk spikes | Checks below pass, or a fallback is selected | Incomplete: Linux checks unverified |
| 1 | Remove unused code and packages; retarget to .NET 10 | Build and publish on Windows | Build verified; hardware behavior unverified |
| 2 | Extract Core and Windows adapters; move lifecycle into services; immutable state snapshots | Windows behavior unchanged with FTMS and WalkingPad | Build, publish and Windows close/relaunch smoke checks pass; hardware gate unverified |
| 3 | Replace Avalonia/WinForms shell with PhotinoX.Blazor; replace WebView logging and fatal dialog | Desktop risk gate passes; Windows UI and release verified | Pending |
| 4 | BlueZ Linux adapter; RtMidi; Linux OpenVR loader and AppImage | Linux BLE risk gate passes; both treadmill types stable on Linux; OSCQuery checked under Proton | Pending |
| 5 | Skia HUD and interactive controller wrist panel; overlay settings and bindings | Overlay risk gate passes; both OSes render and accept clicks without a visible VRChat frame-time hit | Pending |

Phase 1 deleted the spike programs before the full Phase 0 gate was satisfied.
That deletion is not evidence of a passed gate. The runnable programs remain on
`linux-phase-0` (commit `f2d43f7`), including `spikes/README.md` and its commands.
Phases 3–5 must not be accepted until their relevant risk checks pass or a
fallback is explicitly selected.

## Risk check record

### Desktop shell

- Windows PhotinoX spike was reported run in the earlier session; complete
  layout, font, animation and total-memory acceptance was not recorded.
- [ ] Windows: layout, fonts/icons and three.js animation accepted; total memory measured.
- [ ] Linux X11: the same checks accepted on WebKitGTK.
- [ ] Linux Wayland: the same checks accepted on WebKitGTK.

If WebKitGTK fails the checks, select CefGlue before accepting Phase 3.

### SteamVR overlay

- Windows `SetOverlayRaw` spike was reported working, with approximately
  0.35 ms uploads for a 512×256 panel. This is historical evidence, not a
  fresh measurement or completion of the full gate.
- [ ] Windows: color order, laser-click coordinates, upload timing and VRChat frame timing accepted.
- [ ] Linux: the same checks accepted.

The spike uses raw RGBA uploads. Select the upload method from measured results
before accepting the production HUD and wrist panel in Phase 5.

### Linux Bluetooth

- [ ] FTMS: discovery, service resolution, telemetry, speed range, Request Control indication and reconnect accepted.
- [ ] WalkingPad: discovery, service resolution, telemetry and reconnect accepted.

The spike uses Linux.Bluetooth 6.0.0-pre3. If it fails these checks, select a
direct BlueZ D-Bus adapter behind the same Core contract before accepting Phase 4.

## Boundaries

- Core owns protocols, state, locomotion, OSC, integrations, configuration,
  history and lifecycle. It has no desktop UI, WinRT or NAudio dependency.
- Windows owns WinRT BLE and the current NAudio MIDI adapter. Linux will own
  BlueZ BLE. Core uses `IBluetoothClient` plus its factory and `IMidiOutput`.
- The app chooses platform implementations and supplies application identity
  and startup options. Razor components continue to inject Core services.
- One OpenVR service owns the runtime connection. Phase 5 adds an overlay
  surface boundary and a Skia renderer consuming immutable state snapshots.

Build and publish checks do not replace treadmill, UI or SteamVR runtime checks.

## Phase 2 verification

- Release solution build and framework-dependent `win-x64` publish succeeded.
- Published output contains the OpenVR loader, application manifest, actions,
  controller bindings and desktop web assets.
- Two published-app smoke runs with `--no-vr` opened a window, accepted a
  graceful close, and exited with code 0 and empty stderr. The second run
  confirms the single-instance mutex is released. WebSocket shutdown completed.
- Existing compiler warnings and the Avalonia dependency advisory for
  Tmds.DBus.Protocol 0.16.0 remain. No tests were added.
- Manual connection shutdown, native BLE teardown time, connected-treadmill
  behavior, SteamVR behavior and Linux operation still require acceptance checks.
