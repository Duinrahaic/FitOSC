using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FitOSC.Models;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;

namespace FitOSC.Services.Pulsoid;

/// <summary>
/// Service for connecting to Pulsoid and receiving real-time heart rate data via WebSocket.
/// </summary>
public class PulsoidService : IHostedService, IDisposable
{
    private const string PulsoidWebSocketUrl = "wss://dev.pulsoid.net/api/v1/data/real_time";
    private const int MaxReconnectDelaySeconds = 60;
    private const int InitialReconnectDelaySeconds = 1;

    private readonly ILogger<PulsoidService> _logger;
    private readonly ConfigurationService _configService;
    private readonly AppStateService _appStateService;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    private bool _enabled;
    private string? _apiKey;
    private int _currentReconnectDelay = InitialReconnectDelaySeconds;
    private bool _isConnected;
    private int _lastHeartRate;
    private DateTime _lastHeartRateTime = DateTime.MinValue;

    /// <summary>
    /// Event fired when a new heart rate measurement is received.
    /// </summary>
    public event Action<PulsoidHeartRateEvent>? OnHeartRateReceived;

    /// <summary>
    /// Event fired when connection status changes.
    /// </summary>
    public event Action<bool>? OnConnectionStatusChanged;

    public PulsoidService(ILogger<PulsoidService> logger, ConfigurationService configService, AppStateService appStateService)
    {
        _logger = logger;
        _configService = configService;
        _appStateService = appStateService;

        LoadConfiguration();
    }

    /// <summary>
    /// Whether the service is currently connected to Pulsoid.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Whether the service is enabled in configuration.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// The last received heart rate value.
    /// </summary>
    public int LastHeartRate => _lastHeartRate;

    /// <summary>
    /// The time of the last heart rate measurement.
    /// </summary>
    public DateTime LastHeartRateTime => _lastHeartRateTime;

    private void LoadConfiguration()
    {
        var config = _configService.GetConfiguration();
        _enabled = config.Pulsoid.Enabled;
        _apiKey = config.Pulsoid.ApiKey;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfiguration();

        if (!_enabled || string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation("Pulsoid service is disabled or API key not configured");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Pulsoid service starting...");
        StartConnection();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pulsoid service stopping...");
        await DisconnectAsync();
    }

    /// <summary>
    /// Enable the Pulsoid service with the given API key.
    /// </summary>
    public void Enable(string apiKey)
    {
        _apiKey = apiKey;
        _enabled = true;
        StartConnection();
    }

    /// <summary>
    /// Disable the Pulsoid service.
    /// </summary>
    public async Task DisableAsync()
    {
        _enabled = false;
        await DisconnectAsync();
    }

    /// <summary>
    /// Reconnect to Pulsoid (useful after configuration changes).
    /// </summary>
    public async Task ReconnectAsync()
    {
        LoadConfiguration();

        if (!_enabled || string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogInformation("Pulsoid service is disabled or API key not configured");
            await DisconnectAsync();
            return;
        }

        await DisconnectAsync();
        _currentReconnectDelay = InitialReconnectDelaySeconds;
        StartConnection();
    }

    private void StartConnection()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ConnectionLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch { /* Ignore close errors */ }

            _webSocket.Dispose();
            _webSocket = null;
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* Ignore */ }
            _receiveTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        SetConnectionStatus(false);
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _enabled)
        {
            try
            {
                await ConnectAndReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pulsoid connection error");
                SetConnectionStatus(false);
            }

            if (cancellationToken.IsCancellationRequested || !_enabled)
                break;

            // Exponential backoff for reconnection
            _logger.LogInformation("Pulsoid reconnecting in {Delay} seconds...", _currentReconnectDelay);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_currentReconnectDelay), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _currentReconnectDelay = Math.Min(_currentReconnectDelay * 2, MaxReconnectDelaySeconds);
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        var uri = new Uri($"{PulsoidWebSocketUrl}?access_token={_apiKey}");

        _logger.LogInformation("Connecting to Pulsoid...");

        try
        {
            await _webSocket.ConnectAsync(uri, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "Failed to connect to Pulsoid WebSocket");
            throw;
        }

        _logger.LogInformation("Connected to Pulsoid");
        SetConnectionStatus(true);
        _currentReconnectDelay = InitialReconnectDelaySeconds; // Reset backoff on successful connection

        var buffer = new byte[1024];
        var messageBuffer = new StringBuilder();

        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Pulsoid WebSocket closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = messageBuffer.ToString();
                        messageBuffer.Clear();
                        ProcessMessage(message);
                    }
                }
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "Pulsoid WebSocket error during receive");
                break;
            }
        }

        SetConnectionStatus(false);
    }

    private void ProcessMessage(string message)
    {
        try
        {
            var data = JsonSerializer.Deserialize<PulsoidMessage>(message, JsonOptions);

            if (data?.Data?.HeartRate != null)
            {
                var heartRate = data.Data.HeartRate.Value;
                var measuredAt = data.MeasuredAt.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(data.MeasuredAt.Value).DateTime
                    : DateTime.UtcNow;

                _lastHeartRate = heartRate;
                _lastHeartRateTime = measuredAt;

                // Publish to app state for centralized heart rate handling
                _appStateService.PublishPulsoidHeartRate(heartRate);

                var heartRateEvent = new PulsoidHeartRateEvent
                {
                    HeartRate = heartRate,
                    MeasuredAt = measuredAt
                };

                //_logger.LogDebug("Pulsoid heart rate: {HeartRate} bpm", heartRate);
                OnHeartRateReceived?.Invoke(heartRateEvent);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Pulsoid message: {Message}", message);
        }
    }

    private void SetConnectionStatus(bool connected)
    {
        if (_isConnected != connected)
        {
            _isConnected = connected;
            OnConnectionStatusChanged?.Invoke(connected);

            // Publish connection status to app state
            var status = connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
            _appStateService.PublishInterfaceConnectionStatuses(AppInterface.Pulsoid, status);

            if (!connected)
            {
                _lastHeartRate = 0;
                _appStateService.PublishPulsoidHeartRate(0);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _webSocket?.Dispose();
        _cts?.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Pulsoid WebSocket message format.
/// </summary>
public class PulsoidMessage
{
    [JsonPropertyName("measured_at")]
    public long? MeasuredAt { get; set; }

    [JsonPropertyName("data")]
    public PulsoidData? Data { get; set; }
}

/// <summary>
/// Pulsoid data payload.
/// </summary>
public class PulsoidData
{
    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }
}

/// <summary>
/// Event data for heart rate updates.
/// </summary>
public class PulsoidHeartRateEvent
{
    /// <summary>
    /// Heart rate in beats per minute.
    /// </summary>
    public int HeartRate { get; set; }

    /// <summary>
    /// Time when the heart rate was measured.
    /// </summary>
    public DateTime MeasuredAt { get; set; }
}
