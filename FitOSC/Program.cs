using System.Diagnostics;
using Velopack;

namespace FitOSC;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Setup's install, update and uninstall hooks, which exit before the app starts.
        VelopackApp.Build().Run();

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