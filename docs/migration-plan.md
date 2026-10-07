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
| 4 | BlueZ Linux adapter; RtMidi; Linux OpenVR loader and AppImage | Linux BLE risk gate passes; both treadmill types stable on Linux; OSCQuery checked under Proton | Implementation, final Windows/Linux publishes and refreshed AppImage verified; independent reviews found no blockers; desktop, hardware and CI/release acceptance unverified |
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
- Windows owns WinRT BLE; Linux owns BlueZ BLE. Core uses `IBluetoothClient`
  plus its factory and `IMidiOutput`. The shared `FitOSC.Platform.Midi` project
  owns the native RtMidi binding and output adapter; the app registers it once
  on both TFMs. This corrects the read-only brief's proposed placement in Core.
  Core has no MIDI backend or operating-system selection logic.
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

## Phase 4 shell/composition implementation

- The Web SDK app targets `net10.0` and adds `net10.0-windows10.0.19041` only on Windows hosts. Project references
  and service composition select Windows or Linux by TFM, with the portable MIDI
  project referenced unconditionally. Windows defaults to `win-x64`; Linux
  publishing explicitly selects `-f net10.0 -r linux-x64`. The existing Linux
  project is now in the solution; its BLE code and the existing MIDI code were
  left untouched by this shell implementation.
- The synchronous STA entry point, mutex ownership, host startup/shutdown and
  outer error catch retain Phase 3 behavior. Windows-only compilation guards
  protect Chromium browser arguments, WebView2 profile setup and the status-bar
  setting. PhotinoX 5.3.5 documents no native status-bar effect on Linux.
  Both windows retain the title, 700x800 fixed size, developer tools, disabled
  context menu and disabled zoom. Linux retains the existing Razor/three.js UI.
- Linux sets GLib's program name to `FitOSC` before constructing Photino.
  `Assets/icon.png` is the existing ICO's 256x256 PNG frame extracted byte-for-byte,
  without conversion. The whole Performance settings section is hidden on Linux;
  the saved Windows hardware-acceleration preference remains intact.
- Linux's fatal dialog uses GTK3 non-variadic APIs, UTF-8 strings and integer
  gboolean values on the main thread. No display makes `gtk_init_check` return
  false and the helper return. Missing GTK writes one stderr line; other errors
  are not hidden. The existing shell catch first writes the original exception
  to stderr and sets exit code 1. Native aborts and Photino's no-display GTK
  initialization behavior are not handled by this managed dialog.
- Core's `OpenVROptions.ApplicationManifestPath` is required and supplied by
  the shell on both TFMs. Windows supplies the bundled manifest as before.
  Linux writes `fitosc.vrmanifest` beside `ConfigurationService.ConfigFilePath`
  (normally `~/.config/FitOSC`), preserving the bundled template's static metadata,
  removing `binary_path_windows` and `action_manifest_path`, and setting
  `binary_path_linux` to the existing absolute `$APPIMAGE` launcher or
  `Environment.ProcessPath`. Temporary `/tmp/.mount*` launchers are rejected.
  `--no-vr` queries the persistent path without creating the manifest. Runtime
  action loading still uses `AppContext.BaseDirectory/SteamVR/actions.json`.
  No overlay or additional OpenVR connection owner was introduced.
- Windows publishes only `openvr_api.dll`, `rtmidi.dll` and the ICO; Linux
  publishes only `libopenvr_api.so`, `librtmidi.so` and the PNG. Root native files
  explicitly copy to both output and publish and remain outside single-file
  bundles. Photino native assets come from its NuGet RID assets. Both outputs
  carry `OpenVR/LICENSE` and `RtMidi/LICENSE`. No native resolver was added.
- Existing OpenVR loaders match official Valve v2.12.14 downloads byte-for-byte.
  The exact tag's BSD-3-Clause license was added, with sources and full hashes in
  `FitOSC/OpenVR/PROVENANCE.md`. RtMidi binaries, provenance, build scripts and
  the shared adapter belong to the separate MIDI work and were not changed here.
- Phase 4 CI now explicitly selects each publish framework and self-contained setting,
  builds the Linux app project on Ubuntu 22.04, and assembles the AppImage.
  Tool/runtime verification and exact publish commands are recorded in
  [Linux packaging](linux-packaging.md). The Linux app project must be built
  instead of the solution on Linux because the solution includes Windows projects.

## Phase 4 initial shell/composition proof (2026-10-07)

Verified on Windows with .NET SDK 10.0.111, without launching the GUI:

- On a Windows host, `dotnet build FitOSC.sln -c Release` passed for both app TFMs and all platform
  projects. The initial build reported nine existing warnings: three Core and
  three Razor warnings per app TFM; the final incremental build reported six
  Razor warnings. There were no errors.
- Framework-dependent single-file publishes passed with these exact commands:

  ```powershell
  dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0-windows10.0.19041 -r win-x64 --self-contained false -p:PublishSelfContained=false -o bin/phase4-shell-win
  dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0 -r linux-x64 --self-contained false -p:PublishSelfContained=false -o bin/phase4-shell-linux
  ```

  With this configuration, the initial `--self-contained false` publishes still
  included the runtime. The explicit `PublishSelfContained=false` reruns were
  checked by reading each final bundle's runtime configuration: it contains
  framework references, not included frameworks. Release packaging must choose
  its self-contained setting explicitly; these are verification outputs only.
- Both final bundle manifests and embedded dependency graphs include the chosen
  platform and shared MIDI assemblies, with the opposite platform absent.
  Both version 6.0 bundles contain 24 entries. Physical publishes contain the
  proper Photino/native loaders and icon, both licenses, all 58 web files and
  all five SteamVR files. The web files, SteamVR files, icons, vendored native
  loaders and licenses match source bytes. Opposite-OS native files/icons are
  absent. Evidence logs and extracted bundle metadata are under ignored `bin/`.
- No tests were added or run. No GUI, BLE, MIDI device, SteamVR, Proton or memory
  acceptance checks were performed by this shell implementation.

Parent-reported environment evidence remains separate: the real Linux .NET
10.0.111 SDK under `/tmp/fitosc-dotnet` built Core and the Linux BLE project with
three existing Core warnings. WSL 1 has no Linux display, BlueZ or ALSA, and DNS
limitations required Windows NuGet offline packages with NuGetAudit disabled.
Docker Ubuntu 22.04 is available to the parent. These facts do not establish
Linux shell, hardware or AppImage acceptance.

Outstanding gates include real X11 and Wayland rendering, icon/window identity,
GTK fatal-dialog display, treadmill discovery/control/reconnect, Windows MIDI
port-name parity, ALSA CC delivery and replug naming, SteamVR actions and stable
AppImage registration/auto-launch, OSCQuery under Proton, and memory measurement.
The initial AppImage evidence used the pre-BLE-correction input. The final
post-correction publish and packaging proof below supersedes that input for
artifact verification; real CI/release checks and hardware acceptance remain
unverified.


## Phase 4 BLE review corrections

Discovery skips only exact missing-device/object errors, requires nonzero RSSI or
Connected evidence, and preserves the existing name-or-service-UUID match.
Presence is an improvement over cached objects, not an absolute live-presence
claim: RSSI can remain stale when another client keeps discovery running or when
passive Advertisement Monitor updates were not in BlueZ's discovery-found list.
Exact error predicates were checked against BlueZ 5.72/5.83 and libdbus
1.12.20/1.14.10; older daemon behavior remains unverified.

Adapter discovery/filter ownership survives failed cleanup and is retried under
the adapter gate. Ownership intent is recorded before each awaited filter/start
mutation, so an unknown reply state triggers awaited rollback. The original
failure still propagates; nonterminal cleanup failures retain ownership for the
next acquire, including NotReady. Failed final device cleanup is handed atomically
to the next connect for that same device path; local watch cleanup remains reachable even
when remote ownership has already ended. No background retry is introduced.
Loss handlers record state without invoking the logger; awaited cleanup logs
loss, so the warning may be delayed until the next client operation. BlueZ owner
changes invalidate the old session, and cleanup suppresses and drains callbacks.
The shared system connection and its continuation behavior remain unchanged.

FTMS enables required Control Point indications and logs response codes. Required
subscription failure throws, the manager owns one Request Control write, and
failed release clears the displayed device and publishes Error from the shared
release method for both connect and disconnect, including disposal failures. Write
strictness remains unchanged. Pairing/encryption, actual indications and ATT
0xFD/0xFE behavior require hardware acceptance; devices lacking the already
required Control Point/Indicate support report Error. No procedure queue, retry
or response timeout was added. No tests or hardware checks were added.


Correction verification on 2026-10-07 used SDK 10.0.111 and offline package
sources with NuGetAudit disabled. Windows Release solution build passed with
nine existing warnings, zero errors; actual WSL Ubuntu Release app build passed
without target overrides with six existing warnings, zero errors:

```powershell
dotnet build FitOSC.sln -c Release --artifacts-path G:/FitOSC-phase4/bin/phase4-review-corrections-windows -p:RestoreSources=C:/Users/Duinrahaic/.nuget/packages -p:NuGetAudit=false
```

```bash
cd /mnt/g/FitOSC-phase4
DOTNET_ROOT=/tmp/fitosc-dotnet /tmp/fitosc-dotnet/dotnet build FitOSC/FitOSC.csproj -c Release --artifacts-path /tmp/fitosc-phase4-review-corrections-linux -p:RestoreSources=/mnt/c/Users/Duinrahaic/.nuget/packages -p:RestorePackagesPath=/tmp/fitosc-phase4-nuget -p:NuGetAudit=false
```

Manual execution of the workflow PowerShell and Bash validation/version
extraction accepted `V2.0.0-Beta` and produced `2.0.0-Beta`. No test files were
added and no real CI, release or hardware run was performed. Final Opus code
review found no blockers and approved republish and repackaging; M1–M5,
L1–L5 and C1–C3 are closed. Its one low-severity C4 follow-up moved release-failure
state handling into the shared release method. Independent narrow review also
approved that change and the discovery/filter ownership-intent correction.
Fresh publish and packaging evidence is recorded below; hardware gates remain
unaccepted.

The narrow corrections built successfully with SDK **10.0.111** and offline
packages (`NuGetAudit=false`): Core on Windows and actual WSL Ubuntu, plus the
Linux platform project on WSL Ubuntu. Each build reported the same three existing
Core warnings and zero errors. Separate output paths were
`bin/phase4-c4-shared-release-windows-20261007`,
`/tmp/fitosc-phase4-c4-shared-release-linux-20261007` and
`/tmp/fitosc-phase4-c4-ownership-linux-20261007`. These are build results, not
fresh published-artifact verification or Phase 4 acceptance.


## Phase 4 final artifact and smoke proof (2026-10-07)

These refreshed artifacts include the shared C4 release-state and discovery/filter
ownership-intent corrections approved by the independent narrow review.

All three refreshed publishes used SDK **10.0.111** and exited 0. Each Windows
publish reported six preexisting warnings; the incremental Linux publish reported
three existing Core warnings. Windows used default NuGet restore auditing; the actual
Ubuntu Linux publish used offline packages with `NuGetAudit=false` and no
targeting overrides:

```powershell
dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0-windows10.0.19041 -r win-x64 --self-contained false -p:PublishSelfContained=false --artifacts-path bin/phase4-final-build-win-fd -o bin/phase4-final-win-fd
dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0-windows10.0.19041 -r win-x64 --self-contained true -p:PublishSelfContained=true --artifacts-path bin/phase4-final-build-win-sc -o bin/phase4-final-win-sc
```

```bash
cd /mnt/g/FitOSC-phase4
/tmp/fitosc-dotnet/dotnet publish FitOSC/FitOSC.csproj -c Release \
  -f net10.0 -r linux-x64 --self-contained true -p:PublishSelfContained=true \
  --artifacts-path /tmp/fitosc-phase4-final-linux-build \
  -p:RestoreSources=/mnt/c/Users/Duinrahaic/.nuget/packages \
  -p:RestorePackagesPath=/tmp/fitosc-phase4-nuget -p:NuGetAudit=false \
  -o /mnt/g/FitOSC-phase4/bin/phase4-final-linux-sc
```

Independent artifact verification (`python
bin/phase4-final-verification/verify_artifacts.py`, exit 0) confirmed Core,
Windows and shared MIDI in both Windows bundles, and Core, Linux and shared
MIDI in Linux, with the opposite platform absent. Windows framework-dependent
runtime configuration references .NET and ASP.NET Core 10.0.0; its smoke run
loaded installed .NET 10.0.11. Both self-contained outputs include .NET and
ASP.NET Core 10.0.11. Windows bundled managed assemblies and runtime configuration
match the final builds byte-for-byte; dependency metadata matches the final
publish graph. Each Windows publish has 72 byte-equal physical files: seven native,
icon and license files, five SteamVR files, 58 project web files and two
framework web files. No NAudio dependencies were found. Five default-restore
asset graphs had all-package auditing enabled and zero NU190 entries; no fresh
standalone advisory audit was performed. Evidence:
`bin/phase4-final-verification/artifact-proof.json`.

The final hidden `--no-vr` smoke script (`powershell -NoProfile -ExecutionPolicy
Bypass -File bin/phase4-final-verification/verify_hidden_smoke.ps1`, exit 0)
ran Windows framework-dependent once and self-contained twice. All runs exited
0 with empty stderr; close-to-exit took 190 ms, 180 ms and 236 ms respectively.
The second self-contained run confirms mutex release. The script pinned each
process handle, located windows by PID and title, and sent `WM_CLOSE` with a
20-second bound. Startup's automatic BLE scan was canceled during shutdown;
no Bluetooth commands or UI actions were issued. Configuration stayed unchanged
and no FitOSC process remained. Evidence: `smoke-proof.json` and
`config-unchanged-proof.json` under `bin/phase4-final-verification/`. Hidden
windows do not establish visual or connected-hardware acceptance.

The corrected Linux self-contained output was packaged and extracted in pinned
Ubuntu 22.04 Docker with pinned tools and `SOURCE_DATE_EPOCH=0`. All 77 publish
files were byte-equal after extraction; desktop-file validation and launcher/icon
symlinks passed. The final AppImage is **45,652,472 bytes**, SHA-256
`f4cc23a49c1f8ad18bbc9b8e86a0eaef47aea28dc7589185bacd3d4e6a9d88c5`.
See [Linux packaging](linux-packaging.md) for the final artifact and evidence
paths. This replaces the pre-BLE image as final artifact proof. Epoch zero differs
from CI's commit timestamp; no bit-reproducibility claim is made.

Final code review and independent narrow review of the shared C4 release-state
and discovery/filter ownership-intent corrections found no blockers.
Real Linux X11/Wayland rendering,
FTMS and WalkingPad discovery/control, pairing, reconnect and connected cleanup,
physical MIDI ports, SteamVR AppImage auto-launch, OSCQuery under Proton, memory
measurement and CI/release gates remain unverified. WSL1's missing GUI, BlueZ,
ALSA and WebKitGTK only support the recorded partial failure-path evidence.
No tests were added or run, and no release was published.

## Phase 5 implementation and non-hardware proof (2026-10-07)

Phase 5 starts from committed Phase 4 (`84bab71`) in `G:\FitOSC-phase5`.
`FitOSC.Overlay` provides a 512x128 head-locked Skia HUD and a 512x256 wrist
panel through Core's `IOpenVROverlay` boundary. Both are disabled by default.
Skia font and raster surfaces are allocated at startup even when disabled;
disabled overlays do not imply zero memory cost. The Integrations tab saves
HUD/wrist/hand settings immediately. Unknown hand enum values are rejected.
The optional `ToggleHud` action has no default binding; existing controller
bindings are unchanged. Wrist clicks feed existing walking action flags only,
including Dynamic trim and Override preset bounds; no belt commands are exposed.

OpenVRService now owns one awaited, serialized logical loop. Async continuations
may run on different OS threads. Only its TryConnect/Disconnect paths initialize
or shut down the runtime. UI native calls are protected against shutdown, while
application events are raised outside the runtime lock. Wake producers are
serialized. StopMonitoring disconnects; Quit disconnects and waits for the
10-second retry cadence. Partial overlay creation cleans up subscriptions and
handles, overlay failures disable only the overlays for that connection, and
renderer disposal is idempotent and never calls OpenVR after shutdown.

`dotnet build FitOSC.sln -c Release` built both app TFMs with zero errors.
The initial full build reported eight existing warnings: two in OSCService and
three UI warnings per app TFM. The final incremental build reported six UI
warnings, zero errors. OpenVRService's CS4014 warning is gone. Both Windows
publishes exited 0 with five existing warnings each:

```powershell
dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0-windows10.0.19041 -r win-x64 --self-contained false -p:PublishSelfContained=false --artifacts-path bin/phase5-build-win-fd -o bin/phase5-win-fd
dotnet publish FitOSC/FitOSC.csproj -c Release -f net10.0-windows10.0.19041 -r win-x64 --self-contained true -p:PublishSelfContained=true --artifacts-path bin/phase5-build-win-sc -o bin/phase5-win-sc
```

The built Windows app and published framework-dependent/self-contained apps
were launched hidden with `--no-vr` and closed by WM_CLOSE. All four launches
exited 0 with empty stderr (built: 243 ms close-to-exit; FD: 178 ms; SC: 115 and
125 ms). Configuration hashes were unchanged. See
`bin/phase5-verification/{built-smoke-proof,smoke-proof}.json`.

A throwaway manual raster program under ignored `bin/phase5-raster` wrote
12 PNGs each on Windows and Ubuntu: Dynamic metric/imperial, Override,
Disabled, manual and disconnected HUD/wrist views. The visible samples show
readable labels, 4.8 KM/H / 3.0 MPH, opaque dark backgrounds, BT/OSC status
colours and disabled adjustment buttons. The program printed RGBA bytes
`17,34,51,255` for that known colour, all six expected HitTest results after
the GL Y flip, and missing-section defaults
`{"HudEnabled":false,"WristEnabled":false,"WristHand":0}`. It contains no
assertions and is not a repository test. Artifacts:
`bin/phase5-raster-windows/*.png`, `bin/phase5-linux/raster/*.png` and their
raster logs. No automated tests were added or run.

[Linux packaging](linux-packaging.md) records actual Docker builds, native
asset/licence/bundle checks, AppImage extraction and the partial no-display
startup check. Static call proof is in
`bin/phase5-verification/static-proof.txt`: Init/Shutdown calls only in
OpenVRService, one stored Task.Run awaited by StopAsync, no discarded loops
and no finalizer.

All hardware gates remain open: colours/readability in SteamVR, both wrist
transforms, glance hysteresis, controller loss/role swaps, all laser clicks,
optional HUD binding, laser interaction with VRChat, SetOverlayRaw timing,
frame timing, SteamVR quit/restart/disconnect/shutdown and Linux X11/Wayland
with VRChat under Proton. Whether retrying VRApplication_Overlay after Quit
relaunches SteamVR is unverified. The earlier desktop/BLE/MIDI/OSCQuery/memory
and CI gates also remain open. Build and headless evidence do not pass them.
