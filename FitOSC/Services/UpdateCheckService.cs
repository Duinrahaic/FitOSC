using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FitOSC.Services;

/// <summary>
/// Service to check for application updates on GitHub
/// </summary>
public class UpdateCheckService : IHostedService, IDisposable
{
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _httpClient;
    private Timer? _timer;
    private bool _updateAvailable;
    private string? _latestVersion;
    private string? _releaseUrl;

    // Check for updates every 6 hours
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    // GitHub repository information
    private const string GitHubOwner = "Duinrahaic";
    private const string GitHubRepo = "FitOSC";

    public event Action? UpdateAvailableChanged;

    public bool UpdateAvailable => _updateAvailable;
    public string? LatestVersion => _latestVersion;
    public string? ReleaseUrl => _releaseUrl;
    public string CurrentVersion => GetCurrentVersion();

    public UpdateCheckService(ILogger<UpdateCheckService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "FitOSC-UpdateChecker");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpdateCheckService starting...");

        // Check immediately on startup
        _ = CheckForUpdatesAsync();

        // Then check periodically
        _timer = new Timer(
            _ => _ = CheckForUpdatesAsync(),
            null,
            CheckInterval,
            CheckInterval
        );

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpdateCheckService stopping...");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Manually trigger an update check
    /// </summary>
    public Task CheckForUpdatesManuallyAsync()
    {
        return CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _logger.LogDebug("Checking for updates...");

            // Get latest release from GitHub
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to check for updates: {StatusCode}", response.StatusCode);
                return;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>();
            if (release == null)
            {
                _logger.LogWarning("Failed to parse GitHub release response");
                return;
            }

            // Get current version from assembly
            var currentVersion = GetCurrentVersion();
            var latestVersion = release.TagName.TrimStart('v');

            _logger.LogDebug("Current version: {CurrentVersion}, Latest version: {LatestVersion}",
                currentVersion, latestVersion);

            // Compare versions
            var updateAvailable = IsNewerVersion(currentVersion, latestVersion);

            if (updateAvailable != _updateAvailable)
            {
                _updateAvailable = updateAvailable;
                _latestVersion = latestVersion;
                _releaseUrl = release.HtmlUrl;

                if (_updateAvailable)
                {
                    _logger.LogInformation("Update available: v{LatestVersion}", _latestVersion);
                }

                UpdateAvailableChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for updates");
        }
    }

    private string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Try to get InformationalVersion first (supports semantic versioning with pre-release tags)
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (infoVersionAttr != null && !string.IsNullOrEmpty(infoVersionAttr.InformationalVersion))
        {
            var version = infoVersionAttr.InformationalVersion;
            // Strip build metadata (everything after '+')
            var plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
            {
                version = version.Substring(0, plusIndex);
            }
            return version;
        }

        // Fallback to numeric version
        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion != null ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}" : "0.0.0";
    }

    private bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        try
        {
            var current = ParseSemanticVersion(currentVersion);
            var latest = ParseSemanticVersion(latestVersion);

            // Compare major.minor.patch
            if (latest.Major > current.Major) return true;
            if (latest.Major < current.Major) return false;

            if (latest.Minor > current.Minor) return true;
            if (latest.Minor < current.Minor) return false;

            if (latest.Patch > current.Patch) return true;
            if (latest.Patch < current.Patch) return false;

            // If versions are equal, compare pre-release tags
            // Stable version (no pre-release) is newer than pre-release
            if (string.IsNullOrEmpty(latest.PreRelease) && !string.IsNullOrEmpty(current.PreRelease))
                return true; // Latest is stable, current is pre-release

            if (!string.IsNullOrEmpty(latest.PreRelease) && string.IsNullOrEmpty(current.PreRelease))
                return false; // Latest is pre-release, current is stable

            // Both have pre-release tags - compare them
            if (!string.IsNullOrEmpty(latest.PreRelease) && !string.IsNullOrEmpty(current.PreRelease))
            {
                return ComparePreRelease(current.PreRelease, latest.PreRelease) < 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private (int Major, int Minor, int Patch, string? PreRelease) ParseSemanticVersion(string version)
    {
        // Handle versions like "1.0.0-beta.1" or "1.0.0"
        var preReleaseSplit = version.Split('-', 2);
        var versionPart = preReleaseSplit[0];
        var preRelease = preReleaseSplit.Length > 1 ? preReleaseSplit[1] : null;

        var parts = versionPart.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var maj) ? maj : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var min) ? min : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var pat) ? pat : 0;

        return (major, minor, patch, preRelease);
    }

    private int ComparePreRelease(string current, string latest)
    {
        // Pre-release precedence: alpha < beta < rc < (empty/stable)
        var precedence = new Dictionary<string, int>
        {
            { "alpha", 1 },
            { "beta", 2 },
            { "rc", 3 }
        };

        var currentBase = GetPreReleaseBase(current);
        var latestBase = GetPreReleaseBase(latest);

        var currentPrecedence = precedence.ContainsKey(currentBase) ? precedence[currentBase] : 0;
        var latestPrecedence = precedence.ContainsKey(latestBase) ? precedence[latestBase] : 0;

        if (currentPrecedence != latestPrecedence)
            return currentPrecedence.CompareTo(latestPrecedence);

        // Same base (e.g., both beta), compare numeric suffix if present
        var currentNum = GetPreReleaseNumber(current);
        var latestNum = GetPreReleaseNumber(latest);

        return currentNum.CompareTo(latestNum);
    }

    private string GetPreReleaseBase(string preRelease)
    {
        var lower = preRelease.ToLowerInvariant();
        if (lower.StartsWith("alpha")) return "alpha";
        if (lower.StartsWith("beta")) return "beta";
        if (lower.StartsWith("rc")) return "rc";
        return preRelease;
    }

    private int GetPreReleaseNumber(string preRelease)
    {
        // Extract number from strings like "beta.1" or "beta1"
        var parts = preRelease.Split('.', '-');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i], out var num))
                return num;
        }
        return 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _httpClient?.Dispose();
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
