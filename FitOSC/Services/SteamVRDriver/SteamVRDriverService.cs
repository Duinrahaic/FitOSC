using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
/// The driver is registered with SteamVR only while FitOSC runs with SteamVR output selected, and it
/// passes the real thumbstick through whenever FitOSC stops sending.
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

    // SteamVR keeps a loaded driver's DLL open, so it runs from a copy outside the app folder that updates replace.
    private static readonly string BundledDriverPath = Path.Combine(AppContext.BaseDirectory, "SteamVR", "driver", "fitosc");
    // A build can leave the driver folder behind without the DLL, so the DLL is what marks the driver as bundled.
    private static bool IsBundled => File.Exists(Path.Combine(BundledDriverPath, "bin", "win64", "driver_fitosc.dll"));
    private static readonly string InstalledDriverPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FitOSC", "SteamVRDriver", "fitosc");

    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    // Sends come from the locomotion timer while shutdown releases from another thread.
    private readonly object _sendLock = new();
    private bool _stopped;
    private readonly byte[] _packet = new byte[16];
    private readonly byte[] _reply = new byte[16];
    private Socket? _socket;
    private bool _registered;
    private LocomotionOutput _output;
    private DateTime _socketOpened;
    private DateTime _lastReply = DateTime.MinValue;
    private ushort _lastFlags;

    public SteamVRDriverStatus Status { get; private set; } = SteamVRDriverStatus.Inactive;

    /// <summary>Whether the user is pushing the real left thumbstick, which takes over from FitOSC.</summary>
    public bool UserOverride => Status == SteamVRDriverStatus.Ready && (_lastFlags & StatusUserOverride) != 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A crash skips unregistering, so reconcile what SteamVR has registered with the saved output.
        _registered = IsRegistered();
        _output = appState.WalkingModeConfig.Output;
        _ = _output == LocomotionOutput.Joystick ? RegisterAsync() : UnregisterAsync();
        appState.AppStateUpdated += OnAppStateUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => ShutdownAsync();

    /// <summary>
    /// Hands the thumbstick back and unregisters the driver, so SteamVR only loads it while FitOSC runs.
    /// </summary>
    public async Task ShutdownAsync()
    {
        appState.AppStateUpdated -= OnAppStateUpdated;
        lock (_sendLock)
        {
            // Later sends are dropped, so a locomotion tick can't take the stick back after it is released.
            _stopped = true;
            ReleaseLocked();
        }
        await UnregisterAsync();
    }

    private void OnAppStateUpdated(AppStateInfo state)
    {
        var output = state.Walking.Config.Output;
        if (output == _output)
            return;
        _output = output;
        _ = output == LocomotionOutput.Joystick ? RegisterAsync() : UnregisterAsync();
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
        if (!IsBundled)
            SetStatus(SteamVRDriverStatus.NotBundled);
        else if (!_registered)
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

    private async Task RegisterAsync()
    {
        await _registrationLock.WaitAsync();
        try
        {
            if (!IsBundled)
            {
                // Don't leave SteamVR loading a driver this build no longer ships.
                if (_registered)
                {
                    await RunVrPathRegAsync("removedriver");
                    _registered = false;
                }
                return;
            }
            // Copy even when already registered, so a driver left registered by a crash still gets updated.
            CopyDriver();
            if (!_registered)
                _registered = await RunVrPathRegAsync("adddriver");
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private async Task UnregisterAsync()
    {
        await _registrationLock.WaitAsync();
        try
        {
            if (!_registered)
                return;
            await RunVrPathRegAsync("removedriver");
            _registered = false;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private void CopyDriver()
    {
        foreach (var source in Directory.EnumerateFiles(BundledDriverPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(InstalledDriverPath, Path.GetRelativePath(BundledDriverPath, source));
            var targetInfo = new FileInfo(target);
            var sourceInfo = new FileInfo(source);
            if (targetInfo.Exists && targetInfo.Length == sourceInfo.Length && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
                continue;

            try
            {
                Directory.CreateDirectory(targetInfo.DirectoryName!);
                File.Copy(source, target, overwrite: true);
            }
            catch (IOException ex)
            {
                // SteamVR has the previous driver loaded; the new one is copied on a later start.
                logger.LogWarning("Could not update {File} while SteamVR is using it: {Message}", target, ex.Message);
            }
        }
    }

    private async Task<bool> RunVrPathRegAsync(string command)
    {
        var vrPathReg = FindVrPathReg();
        if (vrPathReg == null)
        {
            logger.LogWarning("SteamVR not found; cannot {Command} the FitOSC driver", command);
            return false;
        }

        var startInfo = new ProcessStartInfo(vrPathReg) { CreateNoWindow = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(InstalledDriverPath);
        try
        {
            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            logger.LogInformation("vrpathreg {Command} {Path} exited with {Code}", command, InstalledDriverPath, process.ExitCode);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "vrpathreg {Command} failed", command);
            return false;
        }
    }

    /// <summary>Finds vrpathreg through the SteamVR runtime listed in openvrpaths.vrpath.</summary>
    private string? FindVrPathReg() =>
        ReadVrPaths("runtime")?
            .Select(runtime => Path.Combine(runtime, "bin", "win64", "vrpathreg.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Whether openvrpaths.vrpath lists the installed driver as an external driver. An unreadable file counts as
    /// registered, so a registration left by a crash is still removed.
    /// </summary>
    private bool IsRegistered()
    {
        var drivers = ReadVrPaths("external_drivers");
        if (drivers == null)
            return true;

        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstalledDriverPath));
        return drivers.Any(driver => NormalizedPath(driver) is { } path &&
            string.Equals(path, installed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The full path without a trailing separator, or null for an entry that is not a valid path.</summary>
    private static string? NormalizedPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Reads one path list from SteamVR's openvrpaths.vrpath, or null when the file can't be read.</summary>
    private List<string>? ReadVrPaths(string property)
    {
        var pathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        if (!File.Exists(pathsFile))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pathsFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(property, out var paths)
                || paths.ValueKind != JsonValueKind.Array)
                return [];

            return paths.EnumerateArray()
                .Where(path => path.ValueKind == JsonValueKind.String)
                .Select(path => path.GetString()!)
                .Where(path => path.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Could not read {File}: {Message}", pathsFile, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sendLock)
            _socket?.Dispose();
        _registrationLock.Dispose();
    }
}
