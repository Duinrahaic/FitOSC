using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[TreadmillDriver(TreadmillType.WahooKickrRun, "Wahoo KICKR RUN", "FTMS with Wahoo incline control", Experimental = true)]
public class WahooKickrRunTreadmillService(ILogger<WahooKickrRunTreadmillService> logger, AppStateService appState,
    BaseBluetoothClient client) : FTMSTreadmillService(logger, appState, client)
{
    private static readonly Guid WahooControlPoint = Guid.Parse("a026e03e-0a7d-4ab3-97fa-f1500f9feb8b");

    private const byte SetTargetTiltOpcode = 0x11;
    private const byte ResponsePacket = 0xFE;
    private const byte ResultSuccess = 0x01;

    private readonly ILogger _logger = logger;
    private bool _hasWahooControlPoint;

    public override async Task<bool> ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!await base.ConnectAsync(deviceName, cancellationToken))
            return false;

        _hasWahooControlPoint = await Client.SubscribeAsync(WahooControlPoint, OnWahooNotification);
        if (!_hasWahooControlPoint)
            _logger.LogWarning("Wahoo treadmill control point not found; using FTMS incline control");
        return true;
    }

    public override async Task DisconnectAsync()
    {
        _hasWahooControlPoint = false;
        await base.DisconnectAsync();
    }

    public override async Task SetInclineAsync(decimal incline)
    {
        if (!_hasWahooControlPoint)
        {
            await base.SetInclineAsync(incline);
            return;
        }

        var raw = (short)Math.Clamp(Math.Round(incline * 100), short.MinValue, short.MaxValue);
        await Client.WriteAsync(WahooControlPoint, [SetTargetTiltOpcode, (byte)raw, (byte)(raw >> 8)], withResponse: true);
    }

    private void OnWahooNotification(byte[] data)
    {
        if (data.Length >= 3 && data[0] == ResponsePacket && data[2] != ResultSuccess)
            _logger.LogWarning("Wahoo treadmill rejected opcode {Opcode:X2} with result {Result:X2}", data[1], data[2]);
    }
}
