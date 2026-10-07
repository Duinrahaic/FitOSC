using System.Text.Json;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.History;

/// <summary>
/// Service for tracking and managing treadmill session history.
/// Sessions are stored on disk and only loaded when explicitly requested.
/// </summary>
public class HistoryService
{
    private readonly ILogger<HistoryService> _logger;
    private readonly ConfigurationService _configService;
    private readonly AppStateService _appStateService;
    private readonly string _historyFilePath;
    private TreadmillSession? _currentSession;

    // Tracking variables for current session
    private decimal _totalSpeedSum = 0;
    private int _speedSampleCount = 0;
    private decimal _maxSpeed = 0;
    private int _totalHeartRateSum = 0;
    private int _heartRateSampleCount = 0;

    public event EventHandler? SessionHistoryUpdated;

    public HistoryService(
        ILogger<HistoryService> logger,
        ConfigurationService configService,
        AppStateService appStateService)
    {
        _logger = logger;
        _configService = configService;
        _appStateService = appStateService;

        // Store history in user's AppData folder
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fitOscFolder = Path.Combine(appDataFolder, "FitOSC");
        Directory.CreateDirectory(fitOscFolder);

        _historyFilePath = Path.Combine(fitOscFolder, "history.json");

        // Subscribe to app state changes to track sessions
        _appStateService.AppStateUpdated += OnAppStateUpdated;
    }

    /// <summary>
    /// Get all saved sessions. Loads from disk on each call.
    /// </summary>
    public List<TreadmillSession> GetSessions()
    {
        var sessions = LoadSessionsFromDisk();
        return sessions.OrderByDescending(s => s.StartTime).ToList();
    }

    /// <summary>
    /// Get the currently active session
    /// </summary>
    public TreadmillSession? GetCurrentSession()
    {
        return _currentSession;
    }

    /// <summary>
    /// Delete a session by ID
    /// </summary>
    public void DeleteSession(Guid sessionId)
    {
        var sessions = LoadSessionsFromDisk();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            sessions.Remove(session);
            SaveSessionsToDisk(sessions);
            SessionHistoryUpdated?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Deleted session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Clear all session history
    /// </summary>
    public void ClearAllHistory()
    {
        SaveSessionsToDisk(new List<TreadmillSession>());
        SessionHistoryUpdated?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Cleared all session history");
    }

    private void OnAppStateUpdated(AppStateInfo appState)
    {
        var config = _configService.GetConfiguration();

        // Only track if history is enabled
        if (!config.User.HistoryEnabled)
        {
            return;
        }

        // Check if treadmill is running
        var isRunning = appState.TreadmillState == TreadmillState.Running;

        if (isRunning && _currentSession == null)
        {
            // Start new session
            StartSession(appState);
        }
        else if (isRunning && _currentSession != null)
        {
            // Update current session
            UpdateSession(appState);
        }
        else if (!isRunning && _currentSession != null)
        {
            // End session
            EndSession();
        }
    }

    private void StartSession(AppStateInfo appState)
    {
        _currentSession = new TreadmillSession
        {
            StartTime = DateTime.Now,
            DeviceName = appState.DeviceName,
            TreadmillType = _configService.GetConfiguration().Treadmill.TreadmillType.ToString()
        };

        // Reset tracking variables
        _totalSpeedSum = 0;
        _speedSampleCount = 0;
        _maxSpeed = 0;
        _totalHeartRateSum = 0;
        _heartRateSampleCount = 0;

        _logger.LogInformation("Started new session at {StartTime}", _currentSession.StartTime);
    }

    private void UpdateSession(AppStateInfo appState)
    {
        if (_currentSession == null || appState.TreadmillTelemetry == null)
            return;

        var telemetry = appState.TreadmillTelemetry;

        // Update speed statistics
        var speed = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed);
        if (speed != null)
        {
            _totalSpeedSum += speed.Value;
            _speedSampleCount++;
            if (speed.Value > _maxSpeed)
            {
                _maxSpeed = speed.Value;
            }
        }

        // Update distance
        var distance = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.TotalDistance);
        if (distance != null)
        {
            _currentSession.TotalDistance = distance.Value * 1000; // Convert km to meters
        }

        // Update calories
        var calories = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.Calories);
        if (calories != null)
        {
            _currentSession.Calories = (int)calories.Value;
        }

        // Update heart rate statistics
        var heartRate = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.HeartRate);
        if (heartRate != null && heartRate.Value > 0)
        {
            _totalHeartRateSum += (int)heartRate.Value;
            _heartRateSampleCount++;
        }

        // Update elevation gain
        var elevation = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.ElevationGain);
        if (elevation != null)
        {
            _currentSession.ElevationGain = elevation.Value;
        }

        // Update duration
        _currentSession.DurationSeconds = (int)(DateTime.Now - _currentSession.StartTime).TotalSeconds;
    }

    private void EndSession()
    {
        if (_currentSession == null)
            return;

        var config = _configService.GetConfiguration();

        // Set end time
        _currentSession.EndTime = DateTime.Now;

        // Calculate final averages
        if (_speedSampleCount > 0)
        {
            _currentSession.AverageSpeed = _totalSpeedSum / _speedSampleCount;
        }
        _currentSession.MaxSpeed = _maxSpeed;

        if (_heartRateSampleCount > 0)
        {
            _currentSession.AverageHeartRate = _totalHeartRateSum / _heartRateSampleCount;
        }

        // Calculate final duration
        _currentSession.DurationSeconds = (int)(DateTime.Now - _currentSession.StartTime).TotalSeconds;

        // Only save if session meets minimum duration
        var durationMinutes = _currentSession.DurationSeconds / 60;
        if (durationMinutes >= config.User.MinimumSessionDuration)
        {
            // Load existing sessions, append new one, and save
            var sessions = LoadSessionsFromDisk();
            sessions.Add(_currentSession);
            SaveSessionsToDisk(sessions);
            SessionHistoryUpdated?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Session saved: {Duration} minutes, {Distance}m, {Calories} cal",
                durationMinutes, _currentSession.TotalDistance, _currentSession.Calories);
        }
        else
        {
            _logger.LogInformation("Session discarded (too short): {Duration} minutes < {MinDuration} minutes",
                durationMinutes, config.User.MinimumSessionDuration);
        }

        _currentSession = null;
    }

    /// <summary>
    /// Load sessions from disk. Returns empty list if file doesn't exist or on error.
    /// </summary>
    private List<TreadmillSession> LoadSessionsFromDisk()
    {
     try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath);
                var sessions = JsonSerializer.Deserialize<List<TreadmillSession>>(json);

                if (sessions != null)
                {
                    _logger.LogDebug("Loaded {Count} sessions from disk", sessions.Count);
                    return sessions;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session history from disk");
        }

        return new List<TreadmillSession>();
    }

    /// <summary>
    /// Save sessions to disk.
    /// </summary>
    private void SaveSessionsToDisk(List<TreadmillSession> sessions)
    {
        try
        {
            var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_historyFilePath, json);
            _logger.LogDebug("Saved {Count} sessions to disk", sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save session history to disk");
        }
    }
}
