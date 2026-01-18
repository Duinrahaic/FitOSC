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
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    public static IHost? AppHost { get; private set; }

    /// <summary>
    /// When true, SteamVR/OpenVR initialization is disabled. Use --no-vr launch argument.
    /// </summary>
    public static bool DisableVR { get; private set; }


    public void Dispose()
    {
        if (_desktop != null)
        {
            _desktop.Exit -= Exit;
            _desktop = null;
        }

        AppHost?.Dispose();
        AppHost = null;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktop.Exit += Exit;
            _desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _desktop.MainWindow = new ClientWindow
            {
                DataContext = new ClientWindowViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }


    private void Exit(object? sender, EventArgs e)
    {
        Environment.Exit(0);
    }


    internal static void RunAvaloniaAppWithHosting(string[] args, Func<AppBuilder> buildAvaloniaApp)
    {
        // Check for --no-vr flag to disable SteamVR initialization
        DisableVR = args.Contains("--no-vr", StringComparer.OrdinalIgnoreCase);
        if (DisableVR)
        {
            Console.WriteLine("[FitOSC] SteamVR disabled via --no-vr flag");
        }

        var appBuilder = Host.CreateApplicationBuilder(args);
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
            Environment.Exit(0);
        }
    }
}