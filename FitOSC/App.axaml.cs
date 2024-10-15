using System.Windows.Forms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FitOSC.Client.ViewModels;
using FitOSC.Client.Views;
using Application = Avalonia.Application;
using FitOSC.Shared.Services;
using Valve.VR;

namespace FitOSC;

public partial class App : Application, IDisposable
{
    public static IHost? AppHost { get; private set; }
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktop.Exit += Exit;
            _desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _desktop.MainWindow = new ClientWindow()
            {
                DataContext = new ClientWindowViewModel(),
            };
            
            
        }
        base.OnFrameworkInitializationCompleted();
    }
    

    private void Exit(object? sender, EventArgs e)
    {
        // Exit the application
        System.Environment.Exit(0);
    }
    

    internal static void RunAvaloniaAppWithHosting(string[] args, Func<AppBuilder> buildAvaloniaApp)
    {
        var appBuilder = Host.CreateApplicationBuilder(args);
        //appBuilder.Logging.AddDebug();
        appBuilder.Services.AddWindowsFormsBlazorWebView();
        
        #if DEBUG
        appBuilder.Services.AddBlazorWebViewDeveloperTools();
        
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
        catch(Exception ex)
        {
            var result = MessageBox.Show( ex.ToString(), "FitOSC Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);    
            Console.WriteLine(ex);
            Environment.Exit(0);
            
        }
        finally
        {
            //Task.Run(async () => await myApp.StopAsync()).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        _desktop.Exit -= Exit;
        _desktop = null;
        AppHost?.Dispose();
        AppHost = null;
    }
}