using System.Net;
using System.Net.Sockets;
using FitOSC.Models;
using FitOSC.Services.State;

namespace FitOSC.Services.SteamVRDriver;

public enum SteamVRDriverStatus
{
    Inactive,
    NotBundled,
    SteamVRNotFound,
    RestartSteamVR,
    UnsupportedSteamVR,
    NoLeftController,
    Ready,
}

/// <summary>
/// Owns the FitOSC SteamVR driver, which blends walking movement into the left controller's thumbstick.
/// The driver stays registered with SteamVR and passes the real thumbstick through whenever FitOSC stops sending.
/// </summary>
public sealed class SteamVRDriverService(ILogger<SteamVRDriverService> logger, AppStateService appState) : IHostedService, IDisposable
{
    // Keep in sync with SteamVRDriver/src/driver.cpp.
    private const uint Magic = 0x43534F46; // "FOSC"
    private const ushort ProtocolVersion = 1;
    private const int Port = 39574;
    private const ushort MoveActive = 1;
    private const ushort StatusStickFound = 1;
    private const ushort StatusUserOverride = 2;
    private const ushort StatusUnknownInputLayout = 4;

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    private readonly SteamVRDriverRegistration _registration = new(logger);
    // Sends come from the locomotion timer while shutdown releases from another thread.
    private readonly object _sendLock = new();
    private bool _stopped;
    private readonly byte[] _packet = new byte[16];
    private readonly byte[] _reply = new byte[16];
    private Socket? _socket;
    private volatile Task<bool> _registered = Task.FromResult(false);
    private DateTime _socketOpened;
    private DateTime _lastReply = DateTime.MinValue;
    private ushort _lastFlags;

    public SteamVRDriverStatus Status { get; private set; } = SteamVRDriverStatus.Inactive;

    /// <summary>Whether the user is pushing the real left thumbstick, which takes over from FitOSC.</summary>
    public bool UserOverride => Status == SteamVRDriverStatus.Ready && (_lastFlags & StatusUserOverride) != 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Registering at every start also keeps the copy SteamVR loads up to date with this build.
        _registered = Task.Run(_registration.Register, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Shutdown();
        return Task.CompletedTask;
    }

    /// <summary>Hands the thumbstick back. The driver stays registered so SteamVR loads it on its next start.</summary>
    public void Shutdown()
    {
        lock (_sendLock)
        {
            // Later sends are dropped, so a locomotion tick can't take the stick back after it is released.
            _stopped = true;
            ReleaseLocked();
        }
    }

    /// <summary>
    /// Moves the left thumbstick. Values are -1 to 1; positive vertical is forward, positive horizontal is right.
    /// When <paramref name="active"/> is false the driver passes the real thumbstick through.
    /// </summary>
    public void Send(float vertical, float horizontal, bool active)
    {
        lock (_sendLock)
        {
            if (!_stopped)
                SendLocked(vertical, horizontal, active);
        }
    }

    private void SendLocked(float vertical, float horizontal, bool active)
    {
        if (_socket == null)
        {
            _socket = CreateSocket();
            _socketOpened = DateTime.UtcNow;
        }
        ReadReplies();

        BitConverter.TryWriteBytes(_packet.AsSpan(0), Magic);
        BitConverter.TryWriteBytes(_packet.AsSpan(4), ProtocolVersion);
        BitConverter.TryWriteBytes(_packet.AsSpan(6), active ? MoveActive : (ushort)0);
        BitConverter.TryWriteBytes(_packet.AsSpan(8), Math.Clamp(horizontal, -1f, 1f));
        BitConverter.TryWriteBytes(_packet.AsSpan(12), Math.Clamp(vertical, -1f, 1f));
        try
        {
            _socket.Send(_packet);
        }
        catch (SocketException)
        {
            // Nothing listening yet; the status below reports it.
        }

        UpdateStatus();
    }

    /// <summary>
    /// Hands the thumbstick back to the user straight away instead of waiting for the driver's timeout.
    /// </summary>
    public void Release()
    {
        lock (_sendLock)
            ReleaseLocked();
    }

    private void ReleaseLocked()
    {
        if (_socket != null)
        {
            SendLocked(0f, 0f, active: false);
            _socket.Dispose();
            _socket = null;
        }
        SetStatus(SteamVRDriverStatus.Inactive);
    }

    private static Socket CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        socket.Connect(new IPEndPoint(IPAddress.Loopback, Port));
        return socket;
    }

    private void ReadReplies()
    {
        while (true)
        {
            int length;
            try
            {
                length = _socket!.Receive(_reply);
            }
            catch (SocketException)
            {
                // WouldBlock when the queue is empty; ConnectionReset when the driver isn't listening.
                return;
            }

            if (length == 8
                && BitConverter.ToUInt32(_reply, 0) == Magic
                && BitConverter.ToUInt16(_reply, 4) == ProtocolVersion)
            {
                _lastFlags = BitConverter.ToUInt16(_reply, 6);
                _lastReply = DateTime.UtcNow;
            }
        }
    }

    private void UpdateStatus()
    {
        if (!SteamVRDriverRegistration.IsBundled)
            SetStatus(SteamVRDriverStatus.NotBundled);
        else if (!_registered.IsCompleted)
        {
            // Still registering; report once it settles.
        }
        else if (!_registered.IsCompletedSuccessfully || !_registered.Result)
            SetStatus(SteamVRDriverStatus.SteamVRNotFound);
        else if (DateTime.UtcNow - _lastReply > ReplyTimeout)
        {
            // Give a freshly opened link time for its first reply before reporting the driver missing.
            if (DateTime.UtcNow - _socketOpened > ReplyTimeout)
                SetStatus(SteamVRDriverStatus.RestartSteamVR);
        }
        else if ((_lastFlags & StatusStickFound) != 0)
            SetStatus(SteamVRDriverStatus.Ready);
        else if ((_lastFlags & StatusUnknownInputLayout) != 0)
            SetStatus(SteamVRDriverStatus.UnsupportedSteamVR);
        else
            SetStatus(SteamVRDriverStatus.NoLeftController);
    }

    private void SetStatus(SteamVRDriverStatus status)
    {
        if (status == Status)
            return;
        Status = status;
        logger.LogInformation("SteamVR driver status: {Status}", status);
        appState.PublishInterfaceConnectionStatuses(AppInterface.SteamVRDriver, status switch
        {
            SteamVRDriverStatus.Ready => ConnectionStatus.Connected,
            SteamVRDriverStatus.Inactive => ConnectionStatus.Disconnected,
            SteamVRDriverStatus.RestartSteamVR => ConnectionStatus.Connecting,
            _ => ConnectionStatus.Error,
        });
    }

    public void Dispose()
    {
        lock (_sendLock)
            _socket?.Dispose();
    }
}
