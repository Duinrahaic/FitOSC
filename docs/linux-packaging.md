# Linux AppImage packaging (Phase 4)

The Linux x64 AppImage contains the entire self-contained .NET 10 publish output. Packaging does not build or restore the application. Build on Ubuntu 22.04 to retain the native libraries' glibc and C++ runtime requirements. A successful build or package is not Linux desktop/hardware acceptance.

## Publish and package

Install .NET SDK 10, `desktop-file-utils` (provides `desktop-file-validate`) and `file` (required by appimagetool). Packaging also requires Bash, GNU coreutils/findutils and, when downloading the tools, curl with trusted CA certificates.

From the repository root:

```bash
dotnet publish FitOSC/FitOSC.csproj --configuration Release \
  -f net10.0 -r linux-x64 --self-contained true \
  -p:PublishSelfContained=true -p:Version=2.1.0 \
  --output ./publish/linux-x64
SOURCE_DATE_EPOCH="$(git log -1 --format=%ct)" \
  bash build/appimage/package.sh ./publish/linux-x64 ./publish/appimage 2.1.0
```

The app project defaults to `net10.0` and adds `net10.0-windows10.0.19041` only on Windows. Linux restore therefore does not request Windows targets or WindowsDesktop reference packs. Build/publish the **app project** on Linux, not `FitOSC.sln`: the solution directly includes the Windows platform project, which cannot build on Linux. Windows publishes explicitly select `net10.0-windows10.0.19041`; the framework-dependent variant also sets `-p:PublishSelfContained=false` because this project's single-file publishing otherwise included the runtime during verification.

The script accepts `PUBLISH_DIRECTORY OUTPUT_DIRECTORY VERSION`, optionally followed by an appimagetool path and a runtime path. Version is required, contains no `v` prefix, and must have three numeric components with an optional prerelease suffix, such as `2.1.0-beta.1`. Output is `FitOSC-VERSION-x86_64.AppImage`; an existing destination is rejected. Input and output directories cannot contain one another. Paths with spaces are supported.

To use the parent's already downloaded tools without network access:

```bash
bash build/appimage/package.sh ./publish/linux-x64 ./publish/appimage 2.1.0 \
  ./bin/phase4-tools/appimagetool-x86_64.AppImage \
  ./bin/phase4-tools/runtime-x86_64
```

Both supplied and downloaded tools must match the pinned SHA-256 values below. Tools are copied or downloaded into a fresh task-owned `/tmp/fitosc-appimage.*` directory, made executable there, and removed by the exit trap. The script never deletes the input or output directory and does not change global settings or the supplied tools' modes. `SOURCE_DATE_EPOCH` fixes the SquashFS timestamp; it defaults to zero. For reproducible inputs, use the same publish output, version, tools and epoch. This does not guarantee reproducible .NET/native builds across toolchains.

## AppDir and tool provenance

The AppDir contains:

```text
AppDir/
  AppRun -> usr/bin/FitOSC
  FitOSC.desktop
  fitosc.png                 # copied from publish/Assets/icon.png
  .DirIcon -> fitosc.png
  usr/bin/                   # entire publish directory, including assets/licenses
```

`FitOSC.desktop` uses `Type=Application`, `Name=FitOSC`, `Exec=FitOSC`, `Icon=fitosc`, `Terminal=false`, `Categories=Utility;` and `StartupWMClass=FitOSC`. It is checked with `desktop-file-validate`. The apphost, ELF native binaries and existing executable entrypoints receive executable permissions. The root icon follows the AppImage layout; no extra hicolor copy is required for packaging.

| Tool | Pinned download | SHA-256 |
| --- | --- | --- |
| appimagetool 1.9.1, x86_64 | [Official release asset](https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage) | `ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0` |
| Type 2 runtime 20251108, x86_64 | [Official release asset](https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64) | `2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d` |

Packaging invokes `ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 appimagetool --runtime-file runtime AppDir output.AppImage`. No FUSE mount is required to build the AppImage. Hash verification is mandatory, including for offline tool paths; no floating release lookup is used.

## Host dependencies and running

Self-contained .NET publishing includes the managed runtime, not the host's desktop or hardware stack. The AppImage does not bundle GTK/WebKit or other system webview dependencies. Linux x64 hosts need:

- glibc **2.34 or newer** and `libstdc++` providing **GLIBCXX_3.4.30** (GCC 12 runtime or newer), plus `libgcc`; requirements set by `librtmidi.so` and the PhotinoX native library. Ubuntu 22.04 meets both; glibc 2.34 alone is insufficient.
- .NET native dependencies: ICU, OpenSSL 3, CA certificates, GSSAPI/Kerberos and timezone data.
- GTK 3, WebKitGTK **4.1** (including JavaScriptCore 4.1) and `libnotify4`.
- `libasound2` and the ALSA sequencer (`snd-seq`) for MIDI, with device access for the current user.
- BlueZ and access to the system D-Bus for BLE, with the Bluetooth adapter and appropriate host permissions.
- A graphical session/display for the desktop shell.
- FUSE 3 and `fusermount3` for normal AppImage execution, or the extraction mode below.

Example Ubuntu 22.04 host packages (hardware/kernel configuration and permissions must also be satisfied):

```bash
sudo apt-get update
sudo apt-get install libicu70 libssl3 ca-certificates libgssapi-krb5-2 tzdata \
  libstdc++6 libgcc-s1 libgtk-3-0 libwebkit2gtk-4.1-0 libnotify4 \
  libasound2 bluez libfuse3-3 fuse3
chmod +x FitOSC-2.1.0-x86_64.AppImage
./FitOSC-2.1.0-x86_64.AppImage
# Alternative where FUSE mounting is unavailable:
./FitOSC-2.1.0-x86_64.AppImage --appimage-extract-and-run
```

## CI and acceptance evidence

`.github/workflows/ci.yml` builds Windows runtime/no-runtime artifacts and the Linux AppImage using .NET 10. Linux packaging runs on Ubuntu 22.04. The release job waits for both builds and attaches the AppImage alongside the existing two Windows ZIP files. It runs only on version-tag pushes or explicit workflow dispatch; dispatch checks out the selected release tag. Manual tags accept either `v` or `V` (including `V2.0.0-Beta`); the version passed to publishing and packaging strips that prefix. The push trigger remains `v*.*.*`. Branch pushes do not publish releases. Existing release updates fail on artifact errors.

Historical pre-BLE-correction evidence on 2026-10-07:

- Before the project fix, actual Ubuntu SDK **10.0.111** built the full Linux app using temporary Linux targeting overrides and offline `-p:NuGetAudit=false`: six existing warnings, zero errors. Those overrides were subsequently removed from CI and the documented publish command.
- An existing Linux publish launched with `--no-vr`. Inspection of `PhotinoX.Native.so` with `ldd` identified missing WebKitGTK 4.1, JavaScriptCore 4.1 and libnotify4. Launch logged the original DLL-load error and shutdown; GTK then failed without a display. A separate direct launch confirmed exit code 1. This verifies part of the failure path, not GUI acceptance.
- Two Windows published `--no-vr` launches created hidden windows and passed graceful close/relaunch, each with exit code 0 and empty stderr. Close times were 139 ms and 102 ms. Parent logs: `bin/phase4-shell-win/phase4-smoke-{1,2}.{stdout,stderr}.log`.
- Independent pinned Ubuntu 22.04 Docker packaging passed after installing the required `file` utility. Both tool hashes passed, `desktop-file-validate` passed, and the AppImage built and extracted successfully (container exit 0). All 77 published files retained identical SHA-256 hashes after extraction. `AppRun -> usr/bin/FitOSC` and `.DirIcon -> fitosc.png` were verified; the embedded runtime's `--appimage-version` reported `dd6cebe`. Optional appstreamcli/GPG warnings did not prevent packaging.
- That packaging run used `bin/phase4-package-input`, the Linux self-contained publish from before the BLE corrections. Output: `bin/phase4-appimage-output/FitOSC-2.1.0-x86_64.AppImage`, **45,652,472 bytes**, SHA-256 `270b58fe5aec1d309778b8af8a542979e72661497b85217d4527ecb62fbedc43`. Evidence: `bin/phase4-appimage-output/verification.json` and `bin/phase4-appimage-inspection/`. This proves packaging of that input, not GUI/hardware behavior or final migration acceptance. The container was removed; no production release was published.

The shell review identified missing explicit Windows publish frameworks and Linux restore of Windows targets as blockers. Both were corrected: CI selects the Windows framework, and the app's framework list now follows the host OS without global targeting overrides. Implementation verification with SDK **10.0.111** passed these commands:

```powershell
# Windows: both app targets; nine existing warnings, zero errors.
dotnet build FitOSC/FitOSC.csproj --configuration Release -p:NuGetAudit=false `
  --source C:/Users/Duinrahaic/.nuget/packages `
  -p:ArtifactsPath=G:/FitOSC-phase4/bin/phase4-packaging-windows-build
```

```bash
# Actual WSL Ubuntu: default app target, six existing warnings, zero errors.
cd /mnt/g/FitOSC-phase4
export DOTNET_ROOT=/tmp/fitosc-dotnet
export NUGET_PACKAGES=/tmp/fitosc-phase4-nuget
/tmp/fitosc-dotnet/dotnet build FitOSC/FitOSC.csproj --configuration Release \
  -p:NuGetAudit=false --source /tmp/fitosc-phase4-nuget \
  --source /mnt/c/Users/Duinrahaic/.nuget/packages \
  -p:ArtifactsPath=/mnt/g/FitOSC-phase4/bin/phase4-packaging-linux-build
```

Before the BLE corrections, the parent also verified a native Ubuntu SDK **10.0.111** self-contained publish without targeting overrides (exit 0, six existing warnings):

```bash
cd /mnt/g/FitOSC-phase4
/tmp/fitosc-dotnet/dotnet publish FitOSC/FitOSC.csproj -c Release \
  -f net10.0 -r linux-x64 --self-contained true -p:PublishSelfContained=true \
  --artifacts-path /tmp/fitosc-phase4-linux-publish-build \
  -p:RestoreSources=/mnt/c/Users/Duinrahaic/.nuget/packages \
  -p:RestorePackagesPath=/tmp/fitosc-phase4-nuget -p:NuGetAudit=false \
  -o /tmp/fitosc-phase4-linux-selfcontained
```

The output contains the ELF x86-64 apphost, expected Linux native libraries and PNG, with Windows DLLs absent. This publish and the AppImage evidence above precede the BLE corrections. The final evidence below supersedes that input for artifact verification.

Linux GUI, BLE, physical MIDI and OpenVR acceptance remain unverified. The OSCQuery discovery/VRChat Proton test gate remains **pending**. Local WSL Ubuntu 24 (WSL1) has no GUI, BlueZ or ALSA, and broken DNS; it cannot establish those acceptance gates. No production release workflow is run as part of this packaging change.


## Final corrected package proof (2026-10-07)

The final actual Ubuntu publish used SDK **10.0.111**, the default Linux target
without targeting overrides, offline package sources and `NuGetAudit=false`.
It exited 0 with six preexisting warnings. Its exact command and final Windows
publish/artifact/smoke results are recorded in
[the migration plan](migration-plan.md#phase-4-final-artifact-and-smoke-proof-2026-10-07).
Input was `bin/phase4-final-linux-sc`, including the BLE corrections.

Packaging ran in pinned Ubuntu 22.04 Docker with `desktop-file-utils`, `file`
and `ca-certificates`. Supplied tools passed the pinned SHA-256 checks;
`SOURCE_DATE_EPOCH=0 bash build/appimage/package.sh /publish /out 2.1.0`
built the AppImage, and extraction completed with container exit 0. All 77
publish files were byte-equal after extraction. `desktop-file-validate` passed;
`AppRun -> usr/bin/FitOSC` and `.DirIcon -> fitosc.png` were verified, and the
embedded runtime reported version `dd6cebe`.

Final artifact: `bin/phase4-final-appimage/FitOSC-2.1.0-x86_64.AppImage`,
**45,652,472 bytes**, SHA-256
`47f7642f82d7e305804677edd0bca7a3403c62e14b78c071febcfb0aebc34c6e`.
Evidence: `bin/phase4-final-appimage/verification.json` and
`bin/phase4-final-appimage-inspection/`. The container was removed. This corrected
image replaces the historical pre-BLE image as final artifact proof. Epoch zero
differs from CI's commit timestamp, so these results do not claim bit reproducibility.

Independent code review remains pending. Linux X11/Wayland, FTMS/WalkingPad,
pairing, reconnect, connected cleanup, physical MIDI ports, SteamVR AppImage
auto-launch, OSCQuery under Proton, memory and real CI/release gates remain
unverified. Packaging success does not accept those gates. No tests were added
or run, and no production release was published.
