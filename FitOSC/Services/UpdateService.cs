using System.Reflection;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace FitOSC.Services;

public enum UpdateStatus
{
    UpToDate,
    Checking,
    Available,
    Downloading,
    Ready,
    Failed
}

/// <summary>
/// Checks GitHub releases on the chosen channel. With automatic updates on, new versions download in the
/// background and install on the next start; the app never restarts itself, because the user may be walking in VR.
/// </summary>
public sealed class UpdateService : IHostedService, IDisposable
{
    private const string RepoUrl = "https://github.com/Duinrahaic/FitOSC";

    // Velopack channel names: stable releases are packed on the default Windows channel.
    private const string StableChannel = "win";
    private const string DevChannel = "dev";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ConfigurationService _configService;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private UpdateManager _manager;
    private UpdateInfo? _update;
    private Timer? _timer;

    public event Action? Changed;

    public string CurrentVersion { get; } = ReadAssemblyVersion();

    /// <summary>False when running a build that Setup did not install, such as one started from the IDE.</summary>
    public bool IsInstalled { get; }

    /// <summary>The channel the running build was packed for.</summary>
    public UpdateChannel InstalledChannel { get; }

    public UpdateChannel Channel => _configService.GetConfiguration().Updates.Channel ?? InstalledChannel;
    public UpdateStatus Status { get; private set; } = UpdateStatus.UpToDate;
    public int DownloadProgress { get; private set; }
    public string? NewVersion => _update?.TargetFullRelease.Version.ToString();

    /// <summary>True when the update moves to a lower version, such as switching from Dev back to Stable.</summary>
    public bool IsChannelSwitch => _update?.IsDowngrade == true;

    public UpdateService(ConfigurationService configService, ILogger<UpdateService> logger)
    {
        _configService = configService;
        _logger = logger;

        IsInstalled = new UpdateManager(new GithubSource(RepoUrl, null, false)).IsInstalled;
        InstalledChannel = VelopackLocator.Current.Channel == DevChannel ? UpdateChannel.Dev : UpdateChannel.Stable;
        _manager = CreateManager();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            _logger.LogInformation("Not an installed build; update checks are off");
            return Task.CompletedTask;
        }

        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, CheckInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <summary>Checks for an update and, with automatic updates on, downloads it. Skipped while another check or download runs.</summary>
    public async Task CheckAsync()
    {
        if (!IsInstalled || !await _busy.WaitAsync(0))
            return;

        try
        {
            // Keep showing a known update while re-checking.
            if (_update is null)
                SetStatus(UpdateStatus.Checking);

            _update = await _manager.CheckForUpdatesAsync();
            if (_update is null)
            {
                SetStatus(UpdateStatus.UpToDate);
                return;
            }

            _logger.LogInformation("Update available: {Version} on {Channel}", NewVersion, Channel);
            if (_manager.UpdatePendingRestart?.Version == _update.TargetFullRelease.Version)
                SetStatus(UpdateStatus.Ready);
            else if (_configService.GetConfiguration().Updates.AutoUpdate)
                await DownloadAsync(_update);
            else
                SetStatus(UpdateStatus.Available);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Downloads the update if needed, then closes FitOSC, installs it and starts the new version.</summary>
    public async Task InstallAsync()
    {
        await _busy.WaitAsync();
        try
        {
            if (_update is null)
                return;
            if (Status != UpdateStatus.Ready)
                await DownloadAsync(_update);

            _logger.LogInformation("Restarting to install {Version}", NewVersion);
            _manager.ApplyUpdatesAndRestart(_update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update install failed");
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Applies saved update settings, then checks on the chosen channel. Returns without waiting for either.</summary>
    public void ReloadSettings()
    {
        if (IsInstalled)
            _ = ReloadSettingsAsync();
    }

    private async Task ReloadSettingsAsync()
    {
        // Waits for a running check or download to finish first.
        await _busy.WaitAsync();
        try
        {
            _manager = CreateManager();
            _update = null;
            SetStatus(UpdateStatus.UpToDate);
        }
        finally
        {
            _busy.Release();
        }

        await CheckAsync();
    }

    private async Task DownloadAsync(UpdateInfo update)
    {
        DownloadProgress = 0;
        SetStatus(UpdateStatus.Downloading);
        await _manager.DownloadUpdatesAsync(update, progress =>
        {
            DownloadProgress = progress;
            Changed?.Invoke();
        });
        SetStatus(UpdateStatus.Ready);
    }

    private UpdateManager CreateManager()
    {
        var channel = Channel;
        var options = new UpdateOptions
        {
            ExplicitChannel = channel == UpdateChannel.Dev ? DevChannel : StableChannel,
            // Leaving the channel this build came from can mean a lower version, e.g. Dev back to Stable.
            AllowVersionDowngrade = channel != InstalledChannel
        };
        return new UpdateManager(new GithubSource(RepoUrl, null, channel == UpdateChannel.Dev), options);
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private static string ReadAssemblyVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        // Drop the "+commit" build metadata the SDK appends.
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _busy.Dispose();
    }
}
