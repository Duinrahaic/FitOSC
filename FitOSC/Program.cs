using System.Diagnostics;
using FitOSC.Services.SteamVRDriver;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;

namespace FitOSC;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Setup's install, update and uninstall hooks, which exit before the app starts.
        // Registering the SteamVR driver at install means SteamVR loads it from its first start.
        var driver = new SteamVRDriverRegistration(NullLogger.Instance);
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => driver.Register())
            .OnBeforeUninstallFastCallback(_ => driver.Unregister())
            .Run();

        var globalMutex = new Mutex(true, @"Local\FitOSC.exe", out var mutexSuccess);
        if (!mutexSuccess)
        {
            Debug.Print("App is already running. Quitting...");
            globalMutex.Close();
            return;
        }

        SetupClient.Start(args);
        globalMutex.Close();
    }
}