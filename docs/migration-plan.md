# Cross-platform migration

FitOSC keeps C# and moves to .NET 10 with separate Core and platform projects.
The desktop retains its Razor UI and three.js display. The Phase 3 Windows
desktop shell is PhotinoX.Blazor 5.3.5. A separate Skia SteamVR HUD and interactive
wrist panel remain planned for Phase 5; Linux AppImage packaging is Phase 4 work.

## Phase status and gates

| Phase | Scope | Acceptance gate | Status |
| --- | --- | --- | --- |
| 0 | UI, overlay and Linux BLE risk spikes | Checks below pass, or a fallback is selected | Incomplete: Linux checks unverified |
| 1 | Remove unused code and packages; retarget to .NET 10 | Build and publish on Windows | Build verified; hardware behavior unverified |
| 2 | Extract Core and Windows adapters; move lifecycle into services; immutable state snapshots | Windows behavior unchanged with FTMS and WalkingPad | Build, publish and Windows close/relaunch smoke checks pass; hardware gate unverified |
| 3 | Replace Avalonia/WinForms shell with PhotinoX.Blazor; replace WebView logging and fatal dialog | Desktop risk gate passes; Windows UI and release verified | Shell implemented; Windows build, publish and close/relaunch smoke verified; visual, hardware and Linux gates unverified |
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

## Phase 3 shell and target-framework sequencing

- The app uses PhotinoX.Blazor 5.3.5 with the existing Razor UI and three.js
  assets. Avalonia, WinForms Blazor WebView, their shell files and the browser
  logging sink are removed. The About credit now names PhotinoX.
- Photino builds the only root service provider. The synchronous STA entry point
  retains the single-instance mutex on its owning thread. Hosted services start
  on a background thread in registration order and stop in reverse order before
  app disposal. A service whose startup fails is included in shutdown. All stops
  share one five-second cancellation token; stop errors go to stderr, set exit
  code 1 and allow remaining cleanup to continue. This is a cancellation budget,
  not a hard deadline for services that ignore cancellation.
- One LogStreamService instance is registered with DI and passed to the Serilog
  LogStreamSink. A Razor LogConsole consumes bounded, ordered subscriptions and
  forwards entries to the browser console. Existing debug commands are preserved.
- Windows owns the user32 fatal-error dialog. The shell retains the Chromium
  arguments and hardware-acceleration preference and uses an isolated WebView2
  profile under LocalAppData/FitOSC/WebView2.
- PhotinoX does not expose the former shell's WebView2 memory tuning while
  minimized, script-dialog, pinch-zoom, swipe-navigation or autofill settings.
  Those settings cannot be carried over to the replacement shell.
- Phase 3 keeps the app on `net10.0-windows10.0.19041` with `win-x64`,
  EnableWindowsTargeting, the Web SDK, single-file publishing, application
  manifest, icon and version. Core remains `net10.0`; Windows remains
  `net10.0-windows10.0.19041`. A plain `net10.0` app cannot reference the Windows
  project (NU1201), so dropping the Windows TFM now would break the project graph.
- Phase 4 will multi-target the app for `net10.0-windows10.0.19041` and `net10.0`,
  choosing platform references and composition by target framework, not RID.
  BlueZ, RtMidi, Linux OpenVR loading and AppImage packaging remain Phase 4 work.
  No Linux build or runtime success is claimed by Phase 3.

## Phase 3 verification

- `dotnet build -c Release` succeeded with no errors. Existing compiler warnings
  remain; the self-contained publish reported six (two CS4014, two CS0414,
  one CS0168 and one CA2017).
- `dotnet publish FitOSC/FitOSC.csproj -c Release -r win-x64 --self-contained false
  -o bin/phase3-verification` succeeded; this verification output is ignored.
- `dotnet publish FitOSC/FitOSC.csproj --configuration Release --output
  bin/phase3-selfcontained --self-contained --runtime win-x64
  -p:Version=2.1.0` succeeded. This uses the CI self-contained publish flags and
  the current project version; CI supplies its version from the release tag.
  The project enables PublishSingleFile, and the output directory is ignored.
- Inspection of the self-contained FitOSC.exe bundle confirmed the app and managed runtime
  assemblies and a runtime configuration including .NET and ASP.NET Core 10.0.11.
- Two self-contained `--no-vr` runs created the Photino window, accepted a normal
  window-close message and exited with code 0 and empty stderr. Relaunch succeeded.
  Close-to-exit took 138 ms and 120 ms; neither run logged a renderer disposal
  timeout, and both completed WebSocket shutdown. The windows were hidden, so
  these checks do not establish visual acceptance.
- Build and both publish outputs contain all 58 physical web assets, including the
  index, CSS, JavaScript and fonts, plus Assets/icon.ico, PhotinoX.Native.dll,
  WebView2Loader.dll, openvr_api.dll and the SteamVR manifest, actions and three
  controller binding files. Web assets match their source bytes. In the
  self-contained output, the icon, OpenVR loader and SteamVR files also match
  their source bytes.
- Restored project asset graphs contain no Avalonia, WinForms Blazor WebView or
  Tmds.DBus packages, and record no NU19 vulnerability advisories. The former
  Avalonia/Tmds.DBus advisory is absent from this restore.
- Framework-dependent `--no-vr` close/relaunch checks also passed, with a tracked
  run exiting with code 0 and empty stderr. Blazor render batches were observed.
- Temporarily removing the icon from the ignored publish output forced a startup
  exception. The Windows fatal-error dialog appeared; dismissing it exited with
  code 1. The published icon was restored afterward.
- Window shutdown logs that the three.js dispose call cannot reach the stopped
  Photino message pump. Native window teardown still completes.
- No tests were added. Window layout, fonts/icons, three.js animation, hardware
  acceleration, connected-treadmill teardown and SteamVR behavior still need
  runtime acceptance. Failure after partial hosted-service startup, Linux
  X11/Wayland and memory checks remain unverified.
