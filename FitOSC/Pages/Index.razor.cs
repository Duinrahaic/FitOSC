using FitOSC.Services.Treadmill;

namespace FitOSC.Pages;

public partial class Index
{
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Treadmill.ConnectAsync("URTM036", TreadmillType.FTMS);
        }
    }
}