using System.Diagnostics;
#if WINDOWS
using FatalErrorDialog = FitOSC.Platform.Windows.FatalErrorDialog;
#else
using FitOSC.Platform.Linux;
#endif
using FitOSC.Services;
using FitOSC.Services.Configuration;
using FitOSC.Services.Logger;
using FitOSC.Services.OpenVR;
using Photino.Blazor;
using PhotinoX.App;

namespace FitOSC;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var globalMutex = new Mutex(true, @"Local\FitOSC.exe", out var mutexSuccess);
        if (!mutexSuccess)
        {
            Debug.Print("App is already running. Quitting...");
            globalMutex.Close();
            return;
        }

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine($"FitOSC fatal error: {ex}");
            FatalErrorDialog.Show(ex.ToString());
        }
        finally
        {
            globalMutex.ReleaseMutex();
        }
    }

    private static void Run(string[] args)
    {
        PhotinoBlazorApp? app = null;
        var started = new List<IHostedService>();
        ILogger<Program>? logger = null;

        try
        {
            var disableVR = args.Contains("--no-vr", StringComparer.OrdinalIgnoreCase);
            if (disableVR)
                Console.WriteLine("[FitOSC] SteamVR disabled via --no-vr flag");

#if WINDOWS
            var applicationManifestPath = Path.Combine(AppContext.BaseDirectory, "SteamVR", "fitosc.vrmanifest");
#else
            var applicationManifestPath = SteamVRApplicationManifest.ManifestPath;
            if (!disableVR)
                SteamVRApplicationManifest.EnsureCreated();
            DesktopIdentity.Initialize();
#endif

            var builder = PhotinoBlazorApp.CreateBuilder(new PhotinoAppOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = "wwwroot"
            });
            var logStream = new LogStreamService();
            builder.Services.AddSingleton(logStream);
            builder.Logging.RegisterLogger(logStream);
            builder.Services.AddSingleton(new OpenVROptions { Disabled = disableVR, ApplicationManifestPath = applicationManifestPath });
            builder.Services.RegisterServices();
            builder.RootComponents.Add<FitOSC.Main>("#app");
            builder.ConfigureMainWindow(window =>
            {
                window
                    .SetTitle("FitOSC")
                    .SetSize(700, 800)
                    .SetResizable(false)
#if WINDOWS
                    .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"))
#else
                    .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png"))
#endif
                    .SetDevToolsEnabled(true)
                    .SetContextMenuEnabled(false)
                    .SetZoomEnabled(false);
#if WINDOWS
                window.SetStatusBarEnabled(false)
                    .SetBrowserControlInitParameters(GetBrowserArguments())
                    .SetUserDataFolder(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FitOSC", "WebView2"));
                window.RegisterCreatedHandler((_, _) =>
                    WindowsWindowSizing.SetContentSize(window, 700, 800));
#endif
            });

            app = builder.Build();
            logger = app.Services.GetRequiredService<ILogger<Program>>();
            Task.Run(async () =>
            {
                foreach (var service in app.Services.GetServices<IHostedService>())
                {
                    // Stop the attempted service too if StartAsync fails after partial startup.
                    started.Add(service);
                    await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }).GetAwaiter().GetResult();

            Environment.ExitCode = app.Run();
        }
        catch (Exception ex)
        {
            logger?.LogCritical(ex, "FitOSC shell failed");
            throw;
        }
        finally
        {
            try
            {
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    for (var i = started.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            await started[i].StopAsync(timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Environment.ExitCode = 1;
                            Console.Error.WriteLine($"Shutdown failed for {started[i].GetType().Name}: {ex}");
                        }
                    }
                }).GetAwaiter().GetResult();
            }
            finally
            {
                try
                {
                    app?.Dispose();
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    Console.Error.WriteLine($"FitOSC app disposal failed: {ex}");
                }
            }
        }
    }

#if WINDOWS
    private static string GetBrowserArguments()
    {
        var browserArgs = new List<string>
        {
            "--enable-experimental-web-platform-features",
            "--disable-background-networking",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-client-side-phishing-detection",
            "--disable-default-apps",
            "--disable-extensions",
            "--disable-hang-monitor",
            "--disable-popup-blocking",
            "--disable-prompt-on-repost",
            "--disable-sync",
            "--disable-translate",
            "--disable-domain-reliability",
            "--disable-component-update",
            "--disk-cache-size=1",
            "--media-cache-size=1",
            "--disable-application-cache",
            "--no-first-run",
            "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required",
            "--metrics-recording-only",
            "--disable-breakpad",
            "--disable-ipc-flooding-protection"
        };
        if (ConfigurationService.ReadConfigurationStatic().User.UseHardwareAcceleration)
        {
            browserArgs.Add("--enable-gpu-rasterization");
            browserArgs.Add("--enable-zero-copy");
        }
        else
        {
            browserArgs.Add("--disable-gpu");
            browserArgs.Add("--disable-gpu-compositing");
        }

        return string.Join(" ", browserArgs);
    }
#endif
}
