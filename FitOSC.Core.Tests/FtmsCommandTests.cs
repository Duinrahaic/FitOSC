using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FitOSC.Core.Tests;

public class FtmsCommandTests
{
    private static readonly Guid ControlPoint = new("00002ad9-0000-1000-8000-00805f9b34fb");

    [Fact]
    public async Task SpeedCommandUsesHundredthsOfKphInLittleEndianOrder()
    {
        var client = new RecordingBluetoothClient();
        await using var service = CreateService(client);

        await service.SetSpeedAsync(12.34m);

        var write = Assert.Single(client.Writes);
        Assert.Equal(ControlPoint, write.Characteristic);
        Assert.Equal(new byte[] { 0x02, 0xD2, 0x04 }, write.Command);
    }

    [Theory]
    [InlineData(TreadmillState.Running, new byte[] { 0x07 })]
    [InlineData(TreadmillState.Stopped, new byte[] { 0x08, 0x01 })]
    [InlineData(TreadmillState.Paused, new byte[] { 0x08, 0x02 })]
    public async Task StateCommandsUseTheFtmsOpcodeAndParameter(TreadmillState command, byte[] expected)
    {
        var client = new RecordingBluetoothClient();
        await using var service = CreateService(client);

        switch (command)
        {
            case TreadmillState.Running: await service.StartAsync(); break;
            case TreadmillState.Stopped: await service.StopAsync(); break;
            case TreadmillState.Paused: await service.PauseAsync(); break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }

        var write = Assert.Single(client.Writes);
        Assert.Equal(ControlPoint, write.Characteristic);
        Assert.Equal(expected, write.Command);
    }

    private static FTMSTreadmillService CreateService(RecordingBluetoothClient client) =>
        new(NullLogger<FTMSTreadmillService>.Instance, new AppStateService(), client);
}
