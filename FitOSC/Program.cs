using Avalonia;
using System;
using System.Diagnostics;

namespace FitOSC;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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