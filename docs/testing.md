# Basic core tests

Install the .NET 10 SDK, then run this from the repository root in Windows PowerShell or a Linux shell:

```sh
dotnet test FitOSC.Core.Tests/FitOSC.Core.Tests.csproj --configuration Release --artifacts-path FitOSC.Core.Tests/obj/test-artifacts
```

The command restores packages, builds the test project and `FitOSC.Core`, and runs 12 test cases. Build artifacts stay under the ignored `obj` directory. No desktop workloads, Bluetooth hardware, or VR runtime are needed. Run the explicit project rather than the whole solution to avoid building platform and application projects.

The suite checks fixed expected KPH/MPH conversions, telemetry and connection snapshot isolation, copying caller-owned telemetry, preserving Pulsoid HR when an FTMS frame disables its HR field, synchronous reentrant state publication order, and FTMS speed/start/stop/pause command bytes. A small fake `IBluetoothClient` records writes and rejects discovery, connection, subscription, and read calls. Tests reference only the core project and do not load platform Bluetooth or OpenVR implementations. There are no sleeps or assertions about notification timing.

The project pins [Microsoft.NET.Test.Sdk 17.14.1](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1), [xUnit 2.9.3](https://www.nuget.org/packages/xunit/2.9.3), and [xunit.runner.visualstudio 3.1.5](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5) for a simple VSTest-style `dotnet test` setup. xUnit v2 is a legacy line receiving security updates; this small suite deliberately uses its established runner setup.

The `Core tests` workflow runs on pull requests, branch pushes, and manual dispatch, using `windows-latest` and `ubuntu-22.04` with .NET 10. It builds `FitOSC/FitOSC.csproj` before running the command above, so each OS also compiles its application and platform services. It neither invokes the release workflow nor creates releases.

These are basic unit checks, not end-to-end validation. They do not verify actual Bluetooth traffic, device responses, native platform integration, OpenVR, UI behavior, packaging, deferred notification throttling, or concurrent publication races. All 12 cases passed locally on Windows and Ubuntu under WSL with .NET SDK 10.0.111. The Linux run used cached packages with NuGet auditing disabled because that environment has no working DNS; CI uses the normal restore command above. The GitHub matrix run remains a separate check.
