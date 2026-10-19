# Validating an experimental treadmill driver

Each experimental driver has its own checklist in this folder. This page covers the steps they share. A driver stays
experimental until someone runs its checklist on the real treadmill and posts the evidence on its pull request.

Stand beside the treadmill, not on it, for the first command of each kind. Keep the safety key attached and a hand
near the console's stop button for every test that moves the belt.

## 1. Get the build

**Preview artifact.** Every push to a driver pull request runs **Driver preview build (Windows)**. Open the pull
request, go to **Checks**, pick that workflow, and download `FitOSC-preview-pr<number>-<driver>-<commit>-win-x64`
from the run's **Artifacts** list (you must be signed in to GitHub), for example
`FitOSC-preview-pr44-ES-Linker-c14084b-win-x64`. Use the run for the newest commit on the pull request, and check that
`PREVIEW-BUILD.txt` inside it names that commit. Artifacts expire after 30 days. To rebuild one, a maintainer re-runs
the workflow from the same page, or runs `gh workflow run driver-preview.yml --ref driver/<name>` (that artifact is
named `FitOSC-preview-manual-<driver>-<commit>-win-x64`).

Extract the artifact to a new folder and run `FitOSC-<driver>-Preview.exe` (for example
`FitOSC-ES-Linker-Preview.exe`; `PREVIEW-BUILD.txt` gives the exact name). The build includes the .NET runtime. It
reports a version such as `2.1.0-preview.pr44.3` under **Settings** > **About**; right-click the exe >
**Properties** > **Details**: **Product version** is that version followed by `+<commit>`. Record it.

**Local build.** From a checkout of the pull request (for example `gh pr checkout <number>`), with the .NET 8 SDK:

```
dotnet publish FitOSC/FitOSC.csproj --configuration Release --runtime win-x64 --self-contained --output preview
```

Run `preview\FitOSC.exe`. Its **Product version** is the project version followed by `+<commit>`.

The preview shares settings (`%APPDATA%\FitOSC\config.json`) and logs with an installed FitOSC, so back up
`config.json` first if you care about it. If **Automatically connect to last known device on startup** is on, the
preview connects to the last treadmill with the last driver as soon as it starts; turn it off before switching
drivers. Leave **Auto-launch with SteamVR** off while testing a preview: SteamVR launches `FitOSC.exe`, not the preview.
FitOSC may offer an update because a preview counts as older than the release; ignore it while testing, because
installing the release replaces the preview.

## 2. Select the driver and connect

1. Power the treadmill on and close any phone app connected to it (most consoles accept one connection).
2. In FitOSC open **Settings** (cog icon) > **Integrations**. Under **Treadmill**, set **Treadmill Type** to the
   driver named in the checklist; experimental drivers are marked **(Experimental)**.
3. Press **Scan for Devices** (10 s), then the plug button beside your treadmill's name. **Currently Connected**
   shows the name once the driver has finished connecting. Press **Save** so the type and device are remembered.
   The first-run wizard offers the same choice under **Select Treadmill Type**.

A failed connection shows its reason under **Currently Connected**, and the log has the details.

## 3. Watching the data

- Main screen: speed and incline (the **+** / **-** controls), pace, distance, elapsed time, calories and heart rate.
  A field shows `--` when the driver does not supply it. Speed, pace and distance follow **Settings** > **User** >
  **Preferred Units**. Incline is a percentage grade, although its control is labelled `DEGREES`.
- **Settings** > **Debug** shows the state, speed, distance, calories, heart rate, incline and pace the driver
  published, with its own Metric/Imperial switch.
- Press **Ctrl+Shift+I** in FitOSC (as **Settings** > **Debug** says) and open the **Console** tab:
  - `FitOSC.getAppState()` prints every published value, including steps and elapsed time.
  - `FitOSC.getDeviceInfo()` lists the connected device's services and characteristics. Attach it to every report.
  - `FitOSC.setSpeed(3.0)` (km/h) and `FitOSC.setIncline(2)` (%) send exact targets.

### Controls FitOSC shows that a driver may not support

These come from the shared FitOSC interface, not from the driver under test. Each checklist says which controls
work.

- **START**, **PAUSE** and **STOP** are enabled whenever a treadmill is connected, even when the driver ignores them.
  Where a checklist says STOP is not supported, stop the belt with the console or the safety key.
- The speed **+** / **-** controls step 0.2 mph (0.32 km/h) from the current speed and are enabled whenever the
  driver reports speed. If the driver reports no speed range, pressing them throws an error inside FitOSC instead of
  sending anything; the checklist says so, and `FitOSC.setSpeed()` is the way to send speed to such a driver.
- The incline **-** control does nothing at 0% or below, so negative incline can only be set from the console or with
  `FitOSC.setIncline()`.

## 4. Evidence to post on the pull request

- Treadmill brand, model, console firmware if shown, and the advertised Bluetooth name.
- The **Product version** from step 1 and the pull request commit it matches.
- `FitOSC.getDeviceInfo()` output.
- The day's log, `%APPDATA%\FitOSC\logs\fitosc-<yyyyMMdd>.log`, covering the whole test. Every command FitOSC sends
  appears there as `Wrote <bytes> to <characteristic>`, which is how the checklists check command bytes.
- For each checklist line: pass or fail, the value FitOSC showed next to the value on the console, and a photo or
  short video where the checklist asks for one.

A checklist line fails when FitOSC disagrees with the console beyond the stated tolerance, when a command does
something other than what is listed, or when the log shows an error or exception for it. Mark a line **not tested**
when the treadmill cannot do it; do not mark it passed.

## 5. Disconnect and reconnect (every driver)

1. While connected and stopped, press the disconnect button under **Currently Connected**. FitOSC shows
   **Not connected**, the main screen clears, and the log reports the session ended.
2. Connect again with the plug button. Data returns within a few seconds.
3. With the belt stopped, switch the treadmill off for 10 s and back on. FitOSC shows it is reconnecting
   (`Lost <name>; reconnecting`) and, once the treadmill is back, connects again without any action. Speed and state
   come from the treadmill again rather than from before the drop.
4. Press **Reconnect** (the circular arrows) under **Currently Connected**: FitOSC disconnects and connects again.
