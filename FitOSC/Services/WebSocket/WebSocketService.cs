using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FitOSC.Services.Configuration;
using FitOSC.Services.State;

namespace FitOSC.Services.WebSocket;

/// <summary>
/// WebSocket server service for broadcasting telemetry data and receiving commands.
/// </summary>
public class WebSocketService : IHostedService, IDisposable
{
    private readonly ILogger<WebSocketService> _logger;
    private readonly AppStateService _appState;
    private readonly ConfigurationService _configService;
    private readonly ConcurrentDictionary<Guid, WebSocketClient> _clients = new();

    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    private int _port = 6547;
    private bool _enabled = true;
    private bool _isRunning;
    private long _lastBroadcastTicks = 0;
    private const long BroadcastIntervalTicks = TimeSpan.TicksPerSecond; // 1 second

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public WebSocketService(ILogger<WebSocketService> logger, AppStateService appState, ConfigurationService configService)
    {
        _logger = logger;
        _appState = appState;
        _configService = configService;

        var config = _configService.GetConfiguration();
        _port = config.WebSocket.Port;
        _enabled = config.WebSocket.Enabled;
    }

    public int Port => _port;
    public bool IsEnabled => _enabled && _isRunning;
    public int ConnectedClients => _clients.Count;

    /// <summary>
    /// Enable the WebSocket server at runtime (starts listening if not already running)
    /// </summary>
    public void Enable()
    {
        if (_isRunning) return;

        _enabled = true;
        _logger.LogInformation("WebSocket service enabling on port {Port}...", _port);

        _cts = new CancellationTokenSource();
        _appState.AppStateUpdated += OnAppStateUpdated;
        _listenerTask = Task.Run(() => RunListenerAsync(_cts.Token), _cts.Token);
        _isRunning = true;
    }

    /// <summary>
    /// Disable the WebSocket server at runtime (stops listening and disconnects clients)
    /// </summary>
    public async Task DisableAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("WebSocket service disabling...");

        _enabled = false;
        _isRunning = false;
        _appState.AppStateUpdated -= OnAppStateUpdated;
        _cts?.Cancel();

        // Close all client connections
        foreach (var (_, client) in _clients.ToArray())
        {
            await client.CloseAsync();
            client.Dispose();
        }
        _clients.Clear();

        _httpListener?.Stop();
        _httpListener?.Close();
        _httpListener = null;

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* Ignore */ }
            _listenerTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("WebSocket service disabled");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("WebSocket service is disabled in configuration");
            return Task.CompletedTask;
        }

        _logger.LogInformation("WebSocket service starting on port {Port}...", _port);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _appState.AppStateUpdated += OnAppStateUpdated;
        _listenerTask = Task.Run(() => RunListenerAsync(_cts.Token), _cts.Token);
        _isRunning = true;

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WebSocket service stopping...");

        _isRunning = false;
        _appState.AppStateUpdated -= OnAppStateUpdated;
        _cts?.Cancel();

        // Close all client connections
        var closeTask = Task.WhenAll(_clients.Values.Select(c => c.CloseAsync()));
        try
        {
            await closeTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch { /* Ignore timeout */ }

        foreach (var (_, client) in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();

        _httpListener?.Stop();
        _httpListener?.Close();

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch { /* Ignore */ }
        }

        _logger.LogInformation("WebSocket service stopped");
    }

    private async Task RunListenerAsync(CancellationToken cancellationToken)
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{_port}/");
            _httpListener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _httpListener.Start();

            _logger.LogInformation("WebSocket server listening on ws://localhost:{Port}/", _port);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync().WaitAsync(cancellationToken);

                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleWebSocketConnectionAsync(context, cancellationToken);
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        var status = JsonSerializer.Serialize(new { status = "ok", clients = _clients.Count }, JsonOptions);
                        var buffer = Encoding.UTF8.GetBytes(status);
                        await context.Response.OutputStream.WriteAsync(buffer, cancellationToken);
                        context.Response.Close();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting WebSocket connection");
                }
            }
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start WebSocket HTTP listener on port {Port}", _port);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket listener error");
        }
    }

    private async Task HandleWebSocketConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid();
        WebSocketClient? client = null;

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            client = new WebSocketClient(clientId, wsContext.WebSocket);

            _clients.TryAdd(clientId, client);
            _logger.LogInformation("WebSocket client connected: {ClientId} (Total: {Count})", clientId, _clients.Count);

            // Send initial state
            var initialState = _appState.GetCurrentAppStateInfo();
            await client.SendAsync(new WebSocketMessage { Type = "state", Data = initialState }, JsonOptions);

            // Listen for incoming messages
            var buffer = new byte[4096];
            while (client.IsOpen && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await client.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await HandleClientMessageAsync(client, message, cancellationToken);
                    }
                }
                catch (WebSocketException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket error for client {ClientId}", clientId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogInformation("WebSocket client disconnected: {ClientId} (Total: {Count})", clientId, _clients.Count);
            client?.Dispose();
        }
    }

    private async Task HandleClientMessageAsync(WebSocketClient client, string message, CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<WebSocketRequest>(message, JsonOptions);
            if (request == null) return;

            switch (request.Command?.ToLowerInvariant())
            {
                case "ping":
                    await client.SendAsync(new WebSocketMessage
                    {
                        Type = "pong",
                        Data = new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    }, JsonOptions);
                    break;

                case "getstate":
                    var state = _appState.GetCurrentAppStateInfo();
                    await client.SendAsync(new WebSocketMessage { Type = "state", Data = state }, JsonOptions);
                    break;

                default:
                    await client.SendAsync(new WebSocketMessage
                    {
                        Type = "error",
                        Data = new { message = $"Unknown command: {request.Command}" }
                    }, JsonOptions);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from client {ClientId}: {Message}", client.Id, message);
        }
    }

    private void OnAppStateUpdated(AppStateInfo state)
    {
        if (_clients.IsEmpty || !_isRunning) return;

        // Throttle broadcasts to once per second (thread-safe)
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastBroadcastTicks);
        if (nowTicks - lastTicks < BroadcastIntervalTicks) return;

        // Try to claim this broadcast slot
        if (Interlocked.CompareExchange(ref _lastBroadcastTicks, nowTicks, lastTicks) != lastTicks) return;

        var message = new WebSocketMessage { Type = "telemetry", Data = state };
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        // Broadcast to all clients (fire and forget, each client handles its own send lock)
        foreach (var (id, client) in _clients.ToArray())
        {
            if (!client.IsOpen)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            _ = client.SendRawAsync(buffer);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _httpListener?.Close();

        foreach (var (_, client) in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();
    }
}

/// <summary>
/// Thread-safe WebSocket client wrapper with send synchronization.
/// </summary>
internal class WebSocketClient : IDisposable
{
    private readonly System.Net.WebSockets.WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public Guid Id { get; }
    public bool IsOpen => !_disposed && _socket.State == WebSocketState.Open;

    public WebSocketClient(Guid id, System.Net.WebSockets.WebSocket socket)
    {
        Id = id;
        _socket = socket;
    }

    public async Task SendAsync(WebSocketMessage message, JsonSerializerOptions options)
    {
        if (_disposed || _socket.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, options);
        var buffer = Encoding.UTF8.GetBytes(json);
        await SendRawAsync(buffer);
    }

    public async Task SendRawAsync(byte[] buffer)
    {
        if (_disposed || _socket.State != WebSocketState.Open) return;

        // Wait for send lock with timeout to prevent deadlocks
        if (!await _sendLock.WaitAsync(TimeSpan.FromSeconds(1)))
            return;

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }
        catch (WebSocketException) { /* Client disconnected */ }
        catch (ObjectDisposedException) { /* Socket disposed */ }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        return await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
    }

    public async Task CloseAsync()
    {
        if (_disposed) return;

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch { /* Ignore close errors */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sendLock.Dispose();
        try { _socket.Dispose(); } catch { }
    }
}

public class WebSocketMessage
{
    public string Type { get; set; } = string.Empty;
    public object? Data { get; set; }
}

public class WebSocketRequest
{
    public string? Command { get; set; }
    public JsonElement? Params { get; set; }
}
