using System.Reactive;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using FitOSC.Client.ViewModels;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Web.WebView2.Core;
using ReactiveUI;

namespace FitOSC.Client.Views;

public partial class ClientWindow : ReactiveWindow<ClientWindowViewModel>
{
    private BlazorWebView? _webView;

    public ClientWindow()
    {
        var rootComponents = new RootComponentsCollection
        {
            new RootComponent("#app", typeof(Main), null)
        };

        Resources.Add("services", App.AppHost!.Services);
        Resources.Add("rootComponents", rootComponents);

        InitializeComponent();

        this.WhenActivated(d => d(ViewModel!.ExitInteraction.RegisterHandler(DoExitAsync)));

        // Get reference to WebView for memory management
        _webView = this.FindControl<BlazorWebView>("WebView");
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Handle window state changes for memory optimization
        if (change.Property == WindowStateProperty && _webView != null)
        {
            var newState = (WindowState?)change.NewValue;
            if (newState == WindowState.Minimized)
            {
                // Reduce memory usage when minimized
                _webView.SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Low);
            }
            else
            {
                // Restore normal memory usage when visible
                _webView.SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Normal);
            }
        }
    }

    private async Task DoExitAsync(InteractionContext<Unit, Unit> ic)
    {
        Close();
        await Task.CompletedTask;
        ic.SetOutput(Unit.Default);
    }
}