using System.Windows.Forms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FitOSC.Client.ViewModels;
using FitOSC.Client.Views;
using FitOSC.Services;
using Application = Avalonia.Application;

namespace FitOSC;

public class App : Application, IDisposable
{
    public static IHost? AppHost { get; private set; }



    public void Dispose()
    {
        StopHost();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new ClientWindow
            {
                DataContext = new ClientWindowViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }


    internal static void RunAvaloniaAppWithHosting(string[] args, Func<AppBuilder> buildAvaloniaApp)
    {
        // Check for --no-vr flag to disable SteamVR initialization
        var disableVR = args.Contains("--no-vr", StringComparer.OrdinalIgnoreCase);
        if (disableVR)
        {
            Console.WriteLine("[FitOSC] SteamVR disabled via --no-vr flag");
        }

        var appBuilder = Host.CreateApplicationBuilder(args);
        appBuilder.Services.AddSingleton(new FitOSC.Services.OpenVR.OpenVROptions { Disabled = disableVR });
        appBuilder.Services.AddWindowsFormsBlazorWebView();
        appBuilder.Services.AddBlazorWebViewDeveloperTools();

        try
        {
            appBuilder.Logging.RegisterLogger();
            appBuilder.Services.RegisterServices();
            AppHost = appBuilder.Build();
            AppHost.Start();
            buildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "FitOSC Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Console.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            StopHost();
        }
    }

    private static void StopHost()
    {
        var host = AppHost;
        if (host == null) return;
        AppHost = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task.Run(() => host.StopAsync(timeout.Token)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Host shutdown failed: {ex}");
            Environment.ExitCode = 1;
        }
        finally
        {
            host.Dispose();
        }
    }
}
