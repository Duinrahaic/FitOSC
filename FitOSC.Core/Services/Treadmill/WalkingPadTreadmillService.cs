using FitOSC.Services.State;
using FitOSC.Utilities.BLE;

namespace FitOSC.Services.Treadmill;

public class WalkingPadTreadmillService(AppStateService appState, IBluetoothClient client)
    : TreadmillService(appState, client)
{
    private static readonly Guid ServiceUuid = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid DataUuid    = Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");

    public override async Task ConnectAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (!await Client.ConnectAsync(ServiceUuid, deviceName, cancellationToken).ConfigureAwait(false))
            return;

        await Client.SubscribeAsync(DataUuid, d => RaiseData(TranslateData(d)), cancellationToken).ConfigureAwait(false);
    }

    public override async Task DisconnectAsync() => await Client.DisconnectAsync().ConfigureAwait(false);
    public override async Task RequestControlAsync(CancellationToken cancellationToken = default) =>
        await Client.WriteAsync(DataUuid, new byte[] { 0x00 }, cancellationToken).ConfigureAwait(false);
    public override async Task StartAsync()  => await Client.WriteAsync(DataUuid, new byte[] { 0x01 }).ConfigureAwait(false);
    public override async Task StopAsync()   => await Client.WriteAsync(DataUuid, new byte[] { 0x02 }).ConfigureAwait(false);
    public override async Task PauseAsync()  => await Client.WriteAsync(DataUuid, new byte[] { 0x03 }).ConfigureAwait(false);
    public override async Task SetSpeedAsync(decimal speed)
    {
        // FTMS uses 0.01 km/h units
        int speedUnits = (int)(speed * 100);

        byte lo = (byte)(speedUnits & 0xFF);
        byte hi = (byte)((speedUnits >> 8) & 0xFF);

        // OpCode 0x02 = "Set Target Speed"
        var payload = new byte[] { 0x02, lo, hi };

        await Client.WriteAsync(DataUuid, payload).ConfigureAwait(false);
    }
    public override async Task SetInclineAsync(decimal incline) => await Task.CompletedTask;

    public override Task GetTreadmillConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task SendCommandAsync(byte[] command) => await Client.WriteAsync(DataUuid, command).ConfigureAwait(false);
    public override TreadmillTelemetry TranslateData(byte[] data)
    {
        if (data.Length < 2) return new TreadmillTelemetry();

        // Example protocol: speed = data[1] * 0.1
        decimal speed = data[1] / 10m;

        return new TreadmillTelemetry
        {
        };
    }
}
