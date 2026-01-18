using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using FitOSC.Services;
using FitOSC.Services.Configuration;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Web.WebView2.Core;

namespace FitOSC.Client;

public class BlazorWebView : NativeControlHost
{
    /// <summary>
    ///     The <see cref="AvaloniaProperty" /> which backs the <see cref="ZoomFactor" /> property.
    /// </summary>
    public static readonly DirectProperty<BlazorWebView, double> ZoomFactorProperty
        = AvaloniaProperty.RegisterDirect<BlazorWebView, double>(
            nameof(ZoomFactor),
            x => x.ZoomFactor,
            (x, y) => x.ZoomFactor = y);

    public static readonly DirectProperty<BlazorWebView, IServiceProvider> ServicesProperty
        = AvaloniaProperty.RegisterDirect<BlazorWebView, IServiceProvider>(
            nameof(Services),
            x => x.Services,
            (x, y) => x.Services = y);

    public static readonly DirectProperty<BlazorWebView, RootComponentsCollection> RootComponentsProperty
        = AvaloniaProperty.RegisterDirect<BlazorWebView, RootComponentsCollection>(
            nameof(RootComponents),
            x => x.RootComponents,
            (x, y) => x.RootComponents = y);

    private Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView? _blazorWebView;
    private CoreWebView2? _coreWebView2;
    private string? _hostPage;
    private IServiceProvider _serviceProvider = default!;
    private Uri? _source;
    private double _zoomFactor = 1.0;


    public string? HostPage
    {
        get
        {
            if (_blazorWebView != null) _hostPage = _blazorWebView.HostPage;
            return _hostPage;
        }

        set
        {
            if (_hostPage != value)
            {
                _hostPage = value;
                if (_blazorWebView != null) _blazorWebView.HostPage = value;
            }
        }
    }

    public Uri? Source
    {
        get
        {
            if (_blazorWebView != null) _source = _blazorWebView.WebView.Source;
            return _source;
        }

        set
        {
            if (_source != value)
            {
                _source = value;
                if (_blazorWebView != null) _blazorWebView.WebView.Source = value;
            }
        }
    }

    public double ZoomFactor
    {
        get
        {
            if (_blazorWebView != null) _zoomFactor = _blazorWebView.WebView.ZoomFactor;
            return _zoomFactor;
        }

        set
        {
            if (_zoomFactor != value)
            {
                _zoomFactor = value;
                if (_blazorWebView != null) _blazorWebView.WebView.ZoomFactor = value;
            }
        }
    }

    public IServiceProvider Services
    {
        get => _serviceProvider;
        set
        {
            _serviceProvider = value;
            if (_blazorWebView != null) _blazorWebView.Services = _serviceProvider;
        }
    }

    public RootComponentsCollection RootComponents { get; set; } = new();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            // Read hardware acceleration setting from config (before DI is available)
            var config = ConfigurationService.ReadConfigurationStatic();
            var useHardwareAcceleration = config.User.UseHardwareAcceleration;

            // Build optimized browser arguments for desktop app
            var browserArgsList = new List<string>
            {
                // Core features
                "--enable-experimental-web-platform-features",

                // Reduce background process overhead
                "--disable-background-networking",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",

                // Disable unused browser features for desktop app
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

                // Minimize cache and storage overhead
                "--disk-cache-size=1",
                "--media-cache-size=1",
                "--disable-application-cache",

                // Security/performance for embedded app
                "--no-first-run",
                "--no-default-browser-check",
                "--autoplay-policy=no-user-gesture-required",

                // Reduce metrics and reporting overhead
                "--metrics-recording-only",
                "--disable-breakpad",

                // Single process optimizations (embedded app)
                "--disable-ipc-flooding-protection"
            };

            // GPU settings based on user preference
            if (!useHardwareAcceleration)
            {
                browserArgsList.Add("--disable-gpu");
                browserArgsList.Add("--disable-gpu-compositing");
            }
            else
            {
                // Enable GPU optimizations when hardware acceleration is on
                browserArgsList.Add("--enable-gpu-rasterization");
                browserArgsList.Add("--enable-zero-copy");
            }

            var browserArgs = string.Join(" ", browserArgsList);

            // Initialize the Blazor WebView
            _blazorWebView = new Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView
            {
                HostPage = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName),
                    "wwwroot\\index.html"),
                Services = _serviceProvider,
                Name = "FitOSCWebView",
                BlazorWebViewInitializing = (sender, e) =>
                {
                    e.EnvironmentOptions = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = browserArgs,
                        // Reduce language overhead - use system language only
                        Language = System.Globalization.CultureInfo.CurrentUICulture.Name,
                        // Disable extra browser features
                        AreBrowserExtensionsEnabled = false,
                        // Use shared memory for better performance
                        IsCustomCrashReportingEnabled = false
                    };
                }
            };
            _blazorWebView.WebView.CoreWebView2InitializationCompleted += (sender, args) =>
            {
                if (args.IsSuccess && _blazorWebView?.WebView.CoreWebView2 is { } core)
                {
                    core.AttachWebViewConsole();

                    // Disable navigation features (not needed for embedded app)
                    core.Settings.IsSwipeNavigationEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.Settings.IsBuiltInErrorPageEnabled = false;
                    core.Settings.IsPinchZoomEnabled = false;
                    core.Settings.IsZoomControlEnabled = false;

                    // Disable context menus (dev tools enabled for debugging)
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                    core.Settings.AreDevToolsEnabled = true;

                    // Disable autofill and password features
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false;

                    // Disable web features not needed for desktop app
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.Settings.IsWebMessageEnabled = true; // Keep for Blazor interop
                    core.Settings.IsScriptEnabled = true; // Required for Blazor

                    // Memory optimization - set to low by default, can be raised when active
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

                    // Store reference for memory management
                    _coreWebView2 = core;
                }
            };

            foreach (var component in RootComponents) _blazorWebView.RootComponents.Add(component);

            return new PlatformHandle(_blazorWebView.Handle, "HWND");
        }

        return base.CreateNativeControlCore(parent);
    }


    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows())
        {
            _blazorWebView?.Dispose();
            _blazorWebView = null;
        }
        else
        {
            base.DestroyNativeControlCore(control);
        }
    }


    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            _blazorWebView?.Dispose();
            _blazorWebView = null;
        }

        base.OnUnloaded(e);
    }

    /// <summary>
    /// Sets the memory usage target level for the WebView2.
    /// Call with Low when the window is minimized, Normal when restored.
    /// </summary>
    public void SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel level)
    {
        if (_coreWebView2 != null)
        {
            _coreWebView2.MemoryUsageTargetLevel = level;
        }
    }
}