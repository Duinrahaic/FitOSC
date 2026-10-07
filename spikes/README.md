# Phase 0 risk tests

Throwaway programs that answer the three questions the cross-platform migration depends on.
They are not part of `FitOSC.sln` and are deleted in Phase 1 once the gate below is recorded.

All three need the .NET 10 SDK. Run each from its folder with `dotnet run -c Release -- <args>`.

## 1. PhotinoUiSpike: does the current UI render in PhotinoX?

Renders FitOSC's real `app.css`, fonts and three.js treadmill display inside PhotinoX.Blazor,
animating speed and walking direction the way the app does.

- Windows: uses WebView2 (already installed with Edge).
- Linux: needs WebKitGTK 4.1 (`libwebkit2gtk-4.1-0` on Debian/Ubuntu, `webkit2gtk4.1` on Fedora).
  Test both an X11 session and a Wayland session.

Record for each OS/session:

- [ ] Layout, fonts and icons look like the current app
- [ ] The three.js treadmill animates smoothly (open DevTools with right-click > Inspect for FPS)
- [ ] Total memory: the page shows the host process only. Add the browser processes:
  Windows Task Manager (FitOSC + msedgewebview2), Linux `ps -C WebKitWebProcess,WebKitNetworkProcess,PhotinoUiSpike -o rss=`

Fallback if WebKitGTK falls short: CefGlue for the desktop window.

## 2. OverlaySpike: can a Skia panel be a clickable SteamVR overlay?

Draws a 512x256 Skia panel and shows it as a SteamVR overlay, uploading pixels with
`IVROverlay::SetOverlayRaw` ten times a second. Start SteamVR first.

- `wrist`: attached to the left controller. Click the blue button with the right-hand laser.
- `hud`: locked 1 m ahead of the headset (display check only).

Record for each OS:

- [ ] The panel appears and the red/green/blue swatches show in that order (confirms RGBA byte order)
- [ ] Clicking the button logs `hit=True` (confirms the bottom-left mouse origin flip)
- [ ] The average `SetOverlayRaw` time stays well under 1 ms (measured 0.35 ms on Windows, 512x256)
- [ ] VRChat frame timing does not visibly change while the spike runs

The panel is uploaded as raw pixels instead of an OpenGL texture: it is small and changes at most
a few times a second, so this avoids an OpenGL context dependency. If uploads are too slow on Linux,
Phase 5 switches the upload to an OpenGL texture behind the same interface.

## 3. BleSpike (Linux only): does BlueZ handle FitOSC's treadmills?

`BleSpike "<device name fragment>" ftms` or `... walkingpad`. Needs BlueZ 5.50 or newer.

Record per treadmill:

- [ ] The device is found and its services resolve
- [ ] Telemetry notifications arrive (start the belt to see values change)
- [ ] FTMS only: the supported speed range reads, and Request Control (0x00) gets an indication
- [ ] The reconnection pass also succeeds

Uses Linux.Bluetooth 6.0.0-pre3: 5.67.1 depends on Tmds.DBus 0.20.0, which has a high-severity
advisory (GHSA-xrw6-gwf8-vvr9); 6.0.0-pre3 depends on the patched 0.92.0 and only adds APIs.

Fallback if BlueZ via Linux.Bluetooth falls short: call BlueZ over D-Bus directly with Tmds.DBus.

## Gate

Phase 1 starts when all three pass on Windows and Linux, or a fallback is chosen for the failing piece.
