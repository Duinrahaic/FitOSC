using FitOSC.Models;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FitOSC.Core.Tests;

public class AppStateServiceTests
{
    [Fact]
    public void SnapshotKeepsTelemetryAndConnectionValuesAfterLaterUpdates()
    {
        var state = new AppStateService();
        var input = new object();
        state.BeginTreadmillInput(input);
        state.PublishTreadmillData(SpeedTelemetry(4.5m), input);
        state.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Connected);
        var snapshot = state.GetCurrentAppStateInfo();

        state.PublishTreadmillData(SpeedTelemetry(7.25m), input);
        state.PublishInterfaceConnectionStatuses(AppInterface.Bluetooth, ConnectionStatus.Disconnected);
        var current = state.GetCurrentAppStateInfo();

        Assert.Equal(4.5m, snapshot.CurrentSpeed);
        Assert.Equal(ConnectionStatus.Connected, snapshot.ConnectionStates[AppInterface.Bluetooth]);
        Assert.True(snapshot.HasMetrics);
        Assert.Equal(7.25m, current.CurrentSpeed);
        Assert.Equal(ConnectionStatus.Disconnected, current.ConnectionStates[AppInterface.Bluetooth]);
        Assert.False(current.HasMetrics);
    }

    [Fact]
    public void PublishingTelemetryCopiesTheCallersMutableValues()
    {
        var state = new AppStateService();
        var input = new object();
        state.BeginTreadmillInput(input);
        var telemetry = SpeedTelemetry(4.5m);
        state.PublishTreadmillData(telemetry, input);
        telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value = 99m;
        telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled = false;
        telemetry.Values.Clear();

        var stored = state.LatestData.Values[TreadmillTelemetryProperty.InstantaneousSpeed];
        Assert.Equal(4.5m, stored.Value);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public void FtmsFrameWithoutHeartRatePreservesPulsoidHeartRate()
    {
        var state = new AppStateService();
        var service = new FTMSTreadmillService(NullLogger<FTMSTreadmillService>.Instance, state,
            new RecordingBluetoothClient());
        var input = service;
        // First seed FTMS HR so the next frame also exercises clearing its accumulated Enabled flag.
        state.PublishTreadmillData(service.TranslateData(new byte[] { 0x00, 0x01, 0xF4, 0x01, 90 }), input);
        state.PublishPulsoidHeartRate(123);
        var withoutHeartRate = service.TranslateData(new byte[] { 0x00, 0x00, 0x58, 0x02 });

        Assert.False(withoutHeartRate.Values[TreadmillTelemetryProperty.HeartRate].Enabled);
        state.PublishTreadmillData(withoutHeartRate, input);
        state.PublishTreadmillState(TreadmillState.Running, input);

        Assert.Equal(123, state.GetCurrentAppStateInfo().CurrentHeartRate);
        Assert.True(state.LatestData.Values[TreadmillTelemetryProperty.HeartRate].Enabled);
        Assert.Equal(6m, state.GetCurrentAppStateInfo().CurrentSpeed);
        Assert.False(withoutHeartRate.Values[TreadmillTelemetryProperty.HeartRate].Enabled);
    }

    [Fact]
    public void ReentrantForcedPublicationsReachEachSubscriberInOrder()
    {
        var state = new AppStateService();
        var input = new object();
        state.BeginTreadmillInput(input);
        var firstSubscriber = new List<TreadmillState>();
        var secondSubscriber = new List<TreadmillState>();
        state.AppStateUpdated += snapshot =>
        {
            firstSubscriber.Add(snapshot.TreadmillState);
            if (snapshot.TreadmillState == TreadmillState.Running)
            {
                state.PublishTreadmillState(TreadmillState.Paused, input);
                state.PublishTreadmillState(TreadmillState.Stopped, input);
            }
        };
        state.AppStateUpdated += snapshot => secondSubscriber.Add(snapshot.TreadmillState);

        state.PublishTreadmillState(TreadmillState.Running, input);

        var expected = new[] { TreadmillState.Running, TreadmillState.Paused, TreadmillState.Stopped };
        Assert.Equal(expected, firstSubscriber);
        Assert.Equal(expected, secondSubscriber);
    }

    private static TreadmillTelemetry SpeedTelemetry(decimal speed)
    {
        var telemetry = new TreadmillTelemetry();
        telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Value = speed;
        telemetry.Values[TreadmillTelemetryProperty.InstantaneousSpeed].Enabled = true;
        return telemetry;
    }
}
