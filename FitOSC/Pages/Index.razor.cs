using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;
using Microsoft.AspNetCore.Components;

namespace FitOSC.Pages;

public partial class Index
{
    [Inject] private ConfigurationService ConfigService { get; set; } = null!;
    [Inject] private AppStateService AppState { get; set; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var config = ConfigService.GetConfiguration();

            // Load user preferences
            AppState.UpdatePreferredUnits(config.User.PreferMetric);

            // Load walking mode configuration from persisted settings
            var walkingModeConfig = new WalkingModeConfiguration
            {
                MaxSpeed = config.WalkingMode.MaxSpeed,
                WalkingTrim = config.WalkingMode.DefaultTrim,
                SmoothingFactor = config.WalkingMode.SmoothingFactor,
                MaxTurnAngle = config.WalkingMode.MaxTurnAngle,
                UpdateIntervalMs = config.WalkingMode.UpdateIntervalMs,
                ThumbstickRampSpeed = config.WalkingMode.ThumbstickRampSpeed
            };
            AppState.UpdateWalkingModeConfig(walkingModeConfig);

            // Auto-connect to last known device if enabled
            // Only auto-connect once per app session (not on WebView reloads)
            if (config.Treadmill.AutoConnect
                && !string.IsNullOrEmpty(config.Treadmill.LastDeviceName)
                && !ConfigService.HasAutoConnected())
            {
                ConfigService.MarkAutoConnected();
                await Treadmill.ConnectAsync(config.Treadmill.LastDeviceName, config.Treadmill.TreadmillType);
            }
        }
    }
}