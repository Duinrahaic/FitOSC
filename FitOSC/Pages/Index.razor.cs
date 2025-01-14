using FitOSC.Shared.Pages;
using Valve.VR;

namespace FitOSC.Pages;

public partial class Index: IDisposable
{
    private MainPage? MainPage { get; set; }

    protected override void OnInitialized()
    {
         Ovr.OnDataUpdateReceived += OnOvrDataUpdateReceived;
         
    }
    
    
    private void SetWalkingState(bool state)
    {
        if (state && !Ovr.IsMonitoring)
        {
            Ovr.StartMonitoring();
        }
        else
        {
            Ovr.StopMonitoring();
        }
    }
    private void OnOvrDataUpdateReceived(OpenVRDataEvent e) => MainPage?.OnOvrDataUpdateReceived(e);
    private void ReleaseUnmanagedResources()
    {
        if (Ovr != null)
        {
            Ovr.OnDataUpdateReceived -= OnOvrDataUpdateReceived;
        }
    }
    
    protected virtual void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            Ovr?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Index()
    {
        Dispose(false);
    }
}