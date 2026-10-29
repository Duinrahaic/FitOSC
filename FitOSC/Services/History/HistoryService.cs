using System.Text.Json;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;
using FitOSC.Services.Treadmill;

namespace FitOSC.Services.History;

/// <summary>
/// Records treadmill sessions from a connected treadmill and persists them to disk.
/// Sessions are loaded from disk only on request.
/// </summary>
public class HistoryService : IHostedService
{
    private readonly ILogger<HistoryService> _logger;
    private readonly ConfigurationService _configService;
    private readonly AppStateService _appStateService;
    private readonly string _historyFilePath;
    private readonly object _sessionLock = new();
    private TreadmillSession? _currentSession;

    // Readings averaged over the session's running time
    private readonly SessionStat _speed = new();
    private readonly SessionStat _heartRate = new();
    private readonly SessionStat _incline = new();
    private readonly SessionStat _met = new();
    private readonly SessionStat _forceOnBelt = new();
    private readonly SessionStat _power = new();

    // Treadmill totals restart on a new workout or reconnect, so the session adds up what each reading gained.
    private readonly SessionCounter _distance = new();
    private readonly SessionCounter _calories = new();
    private readonly SessionCounter _elevation = new();
    private readonly SessionCounter _steps = new();

    // Duration counts running time only, so pauses and reconnects inside a session do not add to it.
    private TimeSpan _runningTime;
    private DateTime? _runningSince;

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
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _appStateService.AppStateUpdated += OnAppStateUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _appStateService.AppStateUpdated -= OnAppStateUpdated;

        lock (_sessionLock)
        {
            EndSession();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get all saved sessions, newest first. Loads from disk on each call.
    /// </summary>
    public List<TreadmillSession> GetSessions()
    {
        try
        {
            return LoadSessionsFromDisk().OrderByDescending(s => s.StartTime).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session history from disk");
            return new List<TreadmillSession>();
        }
    }

    /// <summary>
    /// Clear all session history
    /// </summary>
    public void ClearAllHistory()
    {
        lock (_sessionLock)
        {
            SaveSessionsToDisk(new List<TreadmillSession>());
        }

        SessionHistoryUpdated?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Cleared all session history");
    }

    private void OnAppStateUpdated(AppStateInfo appState)
    {
        lock (_sessionLock)
        {
            TrackSession(appState);
        }
    }

    private void TrackSession(AppStateInfo appState)
    {
        appState.ConnectionStates.TryGetValue(AppInterface.Bluetooth, out var bluetooth);
        var isRunning = appState.TreadmillState == TreadmillState.Running;

        // Sessions are only collected from a connected treadmill
        if (_currentSession == null)
        {
            if (!isRunning || bluetooth != ConnectionStatus.Connected
                || !_configService.GetConfiguration().User.HistoryEnabled)
                return;

            StartSession(appState);
        }

        if (isRunning)
            _runningSince ??= DateTime.Now;
        else
            StopRunningClock();

        // A pause or a reconnect keeps the session open; it ends when the treadmill stops or the connection is closed.
        var ended = bluetooth is ConnectionStatus.Disconnected or ConnectionStatus.Error
                    || (appState.TreadmillState == TreadmillState.Stopped && bluetooth != ConnectionStatus.Connecting);
        if (ended)
        {
            EndSession();
            return;
        }

        UpdateSession(appState, isRunning);
    }

    private void StopRunningClock()
    {
        if (_runningSince is { } since)
            _runningTime += DateTime.Now - since;
        _runningSince = null;
    }

    private int RunningSeconds() =>
        (int)(_runningTime + (_runningSince is { } since ? DateTime.Now - since : TimeSpan.Zero)).TotalSeconds;

    private void StartSession(AppStateInfo appState)
    {
        _currentSession = new TreadmillSession
        {
            StartTime = DateTime.Now,
            DeviceName = appState.DeviceName,
            TreadmillType = _configService.GetConfiguration().Treadmill.TreadmillType.ToString()
        };

        // Reset tracking variables
        _speed.Reset();
        _heartRate.Reset();
        _incline.Reset();
        _met.Reset();
        _forceOnBelt.Reset();
        _power.Reset();
        _distance.Reset();
        _calories.Reset();
        _elevation.Reset();
        _steps.Reset();
        _runningTime = TimeSpan.Zero;
        _runningSince = null;

        _logger.LogInformation("Started new session at {StartTime}", _currentSession.StartTime);
    }

    private void UpdateSession(AppStateInfo appState, bool isRunning)
    {
        if (_currentSession == null || appState.TreadmillTelemetry == null)
            return;

        var telemetry = appState.TreadmillTelemetry;

        if (isRunning)
        {
            Sample(telemetry, TreadmillTelemetryProperty.InstantaneousSpeed, _speed);
            Sample(telemetry, TreadmillTelemetryProperty.Incline, _incline);
            Sample(telemetry, TreadmillTelemetryProperty.MetabolicEquivalent, _met);
            Sample(telemetry, TreadmillTelemetryProperty.ForceOnBelt, _forceOnBelt);
            Sample(telemetry, TreadmillTelemetryProperty.Power, _power);

            // Zero means no monitor is reading
            if (telemetry.GetTelemetryValue(TreadmillTelemetryProperty.HeartRate) is { Enabled: true, Value: > 0 } heartRate)
                _heartRate.Add(heartRate.Value);
        }

        // Update distance
        var distance = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.TotalDistance);
        if (distance is { Enabled: true })
        {
            _currentSession.TotalDistance = _distance.Add(distance.Value); // Telemetry distance is in meters
        }

        // Update calories
        var calories = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.Calories);
        if (calories is { Enabled: true })
        {
            _currentSession.Calories = (int)_calories.Add(calories.Value);
        }

        // Update elevation gain
        var elevation = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.ElevationGain);
        if (elevation is { Enabled: true })
        {
            _currentSession.ElevationGain = _elevation.Add(elevation.Value);
        }

        // Update steps
        var steps = telemetry.GetTelemetryValue(TreadmillTelemetryProperty.StepCount);
        if (steps is { Enabled: true })
        {
            _currentSession.Steps = (int)_steps.Add(steps.Value);
        }

        // Update duration
        _currentSession.DurationSeconds = RunningSeconds();
    }

    private static void Sample(TreadmillTelemetry telemetry, TreadmillTelemetryProperty property, SessionStat stat)
    {
        if (telemetry.GetTelemetryValue(property) is { Enabled: true } reading)
            stat.Add(reading.Value);
    }

    private void EndSession()
    {
        if (_currentSession == null)
            return;

        var session = _currentSession;
        _currentSession = null;

        session.EndTime = DateTime.Now;

        session.AverageSpeed = _speed.Average ?? 0;
        session.MaxSpeed = _speed.Max ?? 0;
        session.AverageHeartRate = _heartRate.Average is { } averageHeartRate ? (int)Math.Round(averageHeartRate) : null;
        session.MaxHeartRate = (int?)_heartRate.Max;
        session.AverageIncline = _incline.Average;
        session.MaxIncline = _incline.Max;
        session.AverageMet = _met.Average;
        session.MaxMet = _met.Max;
        session.AverageForceOnBelt = _forceOnBelt.Average;
        session.MaxForceOnBelt = _forceOnBelt.Max;
        session.AveragePower = _power.Average;
        session.MaxPower = _power.Max;

        StopRunningClock();
        session.DurationSeconds = RunningSeconds();

        var minimumMinutes = _configService.GetConfiguration().User.MinimumSessionDuration;
        if (session.DurationSeconds < minimumMinutes * 60)
        {
            _logger.LogInformation("Session discarded (too short): {Duration}s < {MinDuration} minutes",
                session.DurationSeconds, minimumMinutes);
            return;
        }

        List<TreadmillSession> sessions;
        try
        {
            sessions = LoadSessionsFromDisk();
        }
        catch (Exception ex)
        {
            // Never overwrite a history file we couldn't read
            _logger.LogError(ex, "Failed to load session history; session from {StartTime} not saved",
                session.StartTime);
            return;
        }

        sessions.Add(session);
        SaveSessionsToDisk(sessions);
        _logger.LogInformation("Session saved: {Duration}s, {Distance}m, {Calories} cal",
            session.DurationSeconds, session.TotalDistance, session.Calories);

        SessionHistoryUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Load sessions from disk. Returns an empty list if the file doesn't exist; throws if it can't be read.
    /// </summary>
    private List<TreadmillSession> LoadSessionsFromDisk()
    {
        if (!File.Exists(_historyFilePath))
            return new List<TreadmillSession>();

        // Earlier versions could write null entries into the array
        var json = File.ReadAllText(_historyFilePath);
        return JsonSerializer.Deserialize<List<TreadmillSession?>>(json)?.OfType<TreadmillSession>().ToList()
               ?? new List<TreadmillSession>();
    }

    /// <summary>
    /// Save sessions to disk via a temp file so a crash mid-write can't corrupt the history.
    /// </summary>
    private void SaveSessionsToDisk(List<TreadmillSession> sessions)
    {
        try
        {
            var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempPath = _historyFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _historyFilePath, overwrite: true);
            _logger.LogDebug("Saved {Count} sessions to disk", sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save session history to disk");
        }
    }

    /// <summary>
    /// Adds up a treadmill total across one session. A reading lower than the last means the treadmill restarted the
    /// total (a new workout or a reconnect), so it counts from zero again; the first reading is the baseline.
    /// </summary>
    private sealed class SessionCounter
    {
        private decimal? _last;
        private decimal _total;

        public decimal Add(decimal reading)
        {
            if (_last is { } last)
                _total += reading >= last ? reading - last : reading;
            _last = reading;
            return _total;
        }

        public void Reset()
        {
            _last = null;
            _total = 0;
        }
    }

    /// <summary>
    /// Averages a reading across one session and keeps its peak; both are null until the treadmill sends one.
    /// </summary>
    private sealed class SessionStat
    {
        private decimal _sum;
        private int _count;

        public decimal? Average => _count > 0 ? _sum / _count : null;
        public decimal? Max { get; private set; }

        public void Add(decimal reading)
        {
            _sum += reading;
            _count++;
            if (Max is not { } max || reading > max)
                Max = reading;
        }

        public void Reset()
        {
            _sum = 0;
            _count = 0;
            Max = null;
        }
    }
}
