using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public class KingSmithFtmsTreadmillService(ILogger<KingSmithFtmsTreadmillService> logger, AppStateService appState)
    : FTMSTreadmillService(logger, appState)
{
    private static readonly Guid UnlockCharacteristic = Guid.Parse("d18d2c10-c44c-11e8-a355-529269fb1459");

    private static readonly TimeSpan UnlockDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RequestControlDelay = TimeSpan.FromMilliseconds(500);

    private const int UnlockCodeLength = 6;

    private readonly ILogger _logger = logger;
    private bool? _hasUnlockCharacteristic;

    public override async Task DisconnectAsync()
    {
        _hasUnlockCharacteristic = null;
        await base.DisconnectAsync();
    }

    public override async Task RequestControlAsync()
    {
        _hasUnlockCharacteristic ??= await Client.SubscribeAsync(UnlockCharacteristic, null);
        if (_hasUnlockCharacteristic != true)
        {
            await base.RequestControlAsync();
            return;
        }

        await UnlockAsync();
        await WriteControlPointAsync(new byte[] { 0x00 });
    }

    public override async Task StartAsync()
    {
        await RequestControlAsync();
        await Task.Delay(RequestControlDelay);
        await base.StartAsync();
    }

    private async Task UnlockAsync()
    {
        var name = Client.DeviceName;
        if (name == null || name.Length < UnlockCodeLength)
        {
            _logger.LogError("Cannot unlock KingSmith treadmill: device name {Name} is too short", name);
            return;
        }

        var code = name[^UnlockCodeLength..];
        var payload = new byte[2 + UnlockCodeLength];
        payload[0] = 0x01;
        for (var i = 0; i < UnlockCodeLength; i++)
            payload[2 + i] = Uri.IsHexDigit(code[i]) ? (byte)Convert.ToInt32(code[i].ToString(), 16) : (byte)0;

        _logger.LogInformation("Unlocking KingSmith treadmill {Name}", name);
        await Client.WriteAsync(UnlockCharacteristic, payload, withResponse: true);
        await Task.Delay(UnlockDelay);
    }
}
