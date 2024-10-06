using System.Windows.Forms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FitOSC.Client.ViewModels;
using FitOSC.Client.Views;
using Application = Avalonia.Application;
using FitOSC.Shared.Services;

namespace FitOSC;

public partial class App : Application
{
     public static IHost? AppHost { get; private set; }
  

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup += OnStartup;
            desktop.Exit += Exit;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new ClientWindow()
            {
                DataContext = new ClientWindowViewModel(),
            };
            
            
        }
        base.OnFrameworkInitializationCompleted();
    }
    
    private void OnStartup(object sender, ControlledApplicationLifetimeStartupEventArgs e)
    {
        TrayIcons? trayIcons = Resources["AppTrayIcon"] as TrayIcons;
        
        if(trayIcons == null)
            return;

        var trayIcon = trayIcons.FirstOrDefault();
        if(trayIcon == null)
            return;
        var menu = trayIcon.Menu;
        if(menu == null)
            return;
   
        
         
        
        var openWindowMenuItem = new NativeMenuItem("Open Client");
        openWindowMenuItem.Click += OpenClientWindow;
        menu.Add(openWindowMenuItem);
        
        menu.Add(new NativeMenuItem("-"));

        var exitMenuItem = new NativeMenuItem("Exit");
        exitMenuItem.Click += Exit;
        menu.Add(exitMenuItem);

        
    }

    private void Exit(object? sender, EventArgs e)
    {
        // Exit the application
        System.Environment.Exit(0);
    }

    private void OpenClientWindow(object? sender, EventArgs e)
    {
        var anotherWindow = new ClientWindow();
        anotherWindow.Show();
    }
    

    internal static void RunAvaloniaAppWithHosting(string[] args, Func<AppBuilder> buildAvaloniaApp)
    {
        var appBuilder = Host.CreateApplicationBuilder(args);
        appBuilder.Logging.AddDebug();
        appBuilder.Services.AddWindowsFormsBlazorWebView();
        
        #if DEBUG
        appBuilder.Services.AddBlazorWebViewDeveloperTools();
        
        #endif

        appBuilder.Services.RegisterServices();

        using var myApp = appBuilder.Build();
        AppHost = myApp;
        myApp.Start();
        try
        {
            buildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch(Exception ex)
        {
            var result = MessageBox.Show( ex.ToString(), "FitOSC Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);    
            Console.WriteLine(ex);
            Environment.Exit(0);
            
        }
        finally
        {
            Task.Run(async () => await myApp.StopAsync()).GetAwaiter().GetResult();
        }
    }
}