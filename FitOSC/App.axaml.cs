using System.Windows.Forms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FitOSC.Client.ViewModels;
using FitOSC.Client.Views;
using FitOSC.Shared.Services;
using FitOSC.Shared.Utilities;
using Microsoft.JSInterop;
using Valve.VR;
using Application = Avalonia.Application;

namespace FitOSC;

public class App : Application, IDisposable
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    public static IHost? AppHost { get; private set; }


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
        var appBuilder = Host.CreateApplicationBuilder(args);
        appBuilder.Services.AddWindowsFormsBlazorWebView();
        appBuilder.Services.AddBlazorWebViewDeveloperTools();

#if DEBUG

#endif

        appBuilder.Services.RegisterServices();
        appBuilder.Services.RegisterHostedService<OpenVRService>();
        appBuilder.Services.AddSingleton<OscService>();

    
        
        using var myApp = appBuilder.Build();
        AppHost = myApp;
        
        try
        {
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