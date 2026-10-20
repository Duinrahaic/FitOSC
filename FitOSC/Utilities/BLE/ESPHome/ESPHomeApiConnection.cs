using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FitOSC.Models;
using FitOSC.Utilities.BLE.ESPHome.Protocol;
using Google.Protobuf;
using Noise;

namespace FitOSC.Utilities.BLE.ESPHome;

internal readonly record struct ESPHomePacket(ApiMessageType Type, byte[] Payload);

/// <summary>
/// One encrypted ESPHome native API session. A failed or timed-out session is
/// closed, never downgraded or resumed with outstanding commands.
/// </summary>
internal sealed class ESPHomeApiConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private Transport? _transport;
    private Task? _receiveLoop;
    private Task? _keepAliveLoop;
    private int _closed;

    public bool IsConnected => _transport != null && Volatile.Read(ref _closed) == 0;
    public DeviceInfoResponse? DeviceInfo { get; private set; }
    public string? ApiVersion { get; private set; }
    public event Action<ESPHomePacket>? PacketReceived;
    public event Action<Exception>? ConnectionClosed;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task ConnectAsync(BluetoothConnectionSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        var host = settings.Host.Trim();
        using var deadline = new CancellationTokenSource(ConnectTimeout);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, deadline.Token);
        try
        {
            await OpenSocketAsync(host, settings.Port, timeout.Token);
            _stream = _tcp.GetStream();
            await HandshakeAsync(settings.EncryptionKey.Trim(), timeout.Token);
            _receiveLoop = ReceiveLoopAsync();

            var hello = await RequestAsync(ApiMessageType.HelloRequest,
                new HelloRequest { ClientInfo = "FitOSC", ApiVersionMajor = 1, ApiVersionMinor = 15 },
                ApiMessageType.HelloResponse, HelloResponse.Parser, cancellationToken: timeout.Token);
            ApiVersion = $"{hello.ApiVersionMajor}.{hello.ApiVersionMinor}";
            if (hello.ApiVersionMajor != 1 || hello.ApiVersionMinor < 15)
                throw new ESPHomeConnectionException($"The relay uses ESPHome API {ApiVersion}; FitOSC needs 1.15 or newer. Update the relay to ESPHome 2026.9.1 or newer.");

            DeviceInfo = await RequestAsync(ApiMessageType.DeviceInfoRequest, new DeviceInfoRequest(),
                ApiMessageType.DeviceInfoResponse, DeviceInfoResponse.Parser, cancellationToken: timeout.Token);
            var capabilities = await RequestAsync(ApiMessageType.DeviceCapabilitiesRequest, new DeviceCapabilitiesRequest(),
                ApiMessageType.DeviceCapabilitiesResponse, DeviceCapabilitiesResponse.Parser, cancellationToken: timeout.Token);
            // ACTIVE_CONNECTIONS | RAW_ADVERTISEMENTS, from aioesphomeapi.model.BluetoothProxyFeature.
            const uint requiredFeatures = (1 << 1) | (1 << 5);
            if ((capabilities.BluetoothProxy?.FeatureFlags & requiredFeatures) != requiredFeatures)
                throw new ESPHomeConnectionException("The ESPHome device does not offer an active Bluetooth proxy. Enable bluetooth_proxy with active connections in its firmware.");

            _keepAliveLoop = KeepAliveLoopAsync();
        }
        catch (Exception ex)
        {
            // RequestAsync aborts the session on its own timeouts, so classify by the deadline, not the lifetime.
            var timedOut = !cancellationToken.IsCancellationRequested &&
                           (ex is TimeoutException || (ex is OperationCanceledException && deadline.IsCancellationRequested));
            Abort(new IOException("Could not establish the ESPHome proxy connection. Check its address, encryption key, and firmware."));
            if (timedOut)
                throw new ESPHomeConnectionException($"{host}:{settings.Port} did not finish connecting within {ConnectTimeout.TotalSeconds:0} seconds. Check that the relay is powered on and on this network.", ex);
            throw;
        }
    }

    private async Task OpenSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        try { await _tcp.ConnectAsync(host, port, cancellationToken); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            throw new ESPHomeConnectionException($"Could not find {host} on the network. Check the name, or use the relay's IP address.", ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            throw new ESPHomeConnectionException($"{host} refused port {port}. Check the port, and that the relay's firmware has the native API enabled.", ex);
        }
        catch (SocketException ex)
        {
            throw new ESPHomeConnectionException($"Could not reach {host}:{port} ({ex.SocketErrorCode}). Check the address and that the relay is on this network.", ex);
        }
    }

    private async Task HandshakeAsync(string encryptionKey, CancellationToken cancellationToken)
    {
        var key = Convert.FromBase64String(encryptionKey);
        try
        {
            // Same Noise suite and prologue as aioesphomeapi/_frame_helper/noise.py.
            var protocol = new Noise.Protocol(HandshakePattern.NN, CipherFunction.ChaChaPoly,
                HashFunction.Sha256, PatternModifiers.Psk0);
            using var handshake = protocol.Create(true, Encoding.ASCII.GetBytes("NoiseAPIInit\0\0"), psks: new[] { key });
            await WriteFrameAsync(Array.Empty<byte>(), cancellationToken);
            var outgoing = new byte[ProtocolMaxLength];
            var first = handshake.WriteMessage(Array.Empty<byte>(), outgoing);
            var clientHandshake = new byte[first.BytesWritten + 1];
            outgoing.AsSpan(0, first.BytesWritten).CopyTo(clientHandshake.AsSpan(1));
            await WriteFrameAsync(clientHandshake, cancellationToken);

            byte[] hello;
            try { hello = await ReadFrameAsync(cancellationToken); }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException { InnerException: SocketException })
            {
                // Matches aioesphomeapi's EncryptionHelloAPIError: a plaintext-only API drops encrypted clients here.
                throw new ESPHomeConnectionException("The relay closed the connection at the encrypted hello. Its firmware may not have API encryption enabled.", ex);
            }
            if (hello.Length == 0 || hello[0] != 1)
                throw new ESPHomeConnectionException("The relay offered an encryption protocol FitOSC does not support. Update its ESPHome firmware.");
            var reply = await ReadFrameAsync(cancellationToken);
            if (reply.Length == 0)
                throw new ESPHomeConnectionException("The relay sent an empty encryption handshake.");
            if (reply[0] != 0)
            {
                // ESPHome names a PSK mismatch explicitly; any other reason is not a key problem.
                var reason = Encoding.UTF8.GetString(reply, 1, reply.Length - 1);
                throw new ESPHomeConnectionException(reason == "Handshake MAC failure"
                    ? KeyRejected
                    : $"The relay rejected the encryption handshake: {SanitizeDeviceText(reason)}");
            }
            try
            {
                var completed = handshake.ReadMessage(reply.AsSpan(1), outgoing);
                _transport = completed.Transport ?? throw new IOException("ESPHome encryption handshake did not complete.");
            }
            catch (CryptographicException ex)
            {
                // Noise.NET raises this only when the handshake reply fails authentication.
                throw new ESPHomeConnectionException(KeyRejected, ex);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private const string KeyRejected = "The relay rejected the API encryption key. Copy the key from its api: encryption: key setting.";

    private static string SanitizeDeviceText(string text)
    {
        var printable = new string(text.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        return printable.Length == 0 ? "no reason given" : printable;
    }

    private const int ProtocolMaxLength = 65535;

    public async Task<T> RequestAsync<T>(ApiMessageType requestType, IMessage request,
        ApiMessageType responseType, MessageParser<T> parser,
        Func<T, bool>? matches = null, Func<ESPHomePacket, Exception?>? failure = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null) where T : IMessage<T>
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandlePacket(ESPHomePacket packet)
        {
            try
            {
                var error = failure?.Invoke(packet);
                if (error != null)
                    completion.TrySetException(error);
                else if (packet.Type == responseType)
                {
                    var response = parser.ParseFrom(packet.Payload);
                    if (matches == null || matches(response))
                        completion.TrySetResult(response);
                }
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        void HandleClose(Exception error) => completion.TrySetException(error);

        PacketReceived += HandlePacket;
        ConnectionClosed += HandleClose;
        try
        {
            await SendAsync(requestType, request, cancellationToken);
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            Abort(new IOException("The ESPHome proxy did not respond in time. Reconnect before sending more commands."));
            throw;
        }
        catch (OperationCanceledException)
        {
            // The protocol has no request IDs. Discard this session so a late
            // response cannot be mistaken for the next operation's response.
            Abort(new IOException("The ESPHome operation was cancelled."));
            throw;
        }
        finally
        {
            PacketReceived -= HandlePacket;
            ConnectionClosed -= HandleClose;
        }
    }

    public async Task SendAsync(ApiMessageType type, IMessage message, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _writeLock.WaitAsync(timeout.Token);
        try
        {
            if (!IsConnected)
                throw new IOException("The ESPHome proxy is disconnected. Reconnect before sending commands.");
            var payload = message.ToByteArray();
            if (payload.Length > ProtocolMaxLength - 20)
                throw new ArgumentException("ESPHome API message is too large.");
            var plaintext = new byte[payload.Length + 4];
            BinaryPrimitives.WriteUInt16BigEndian(plaintext, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(2), (ushort)payload.Length);
            payload.CopyTo(plaintext, 4);
            var ciphertext = new byte[plaintext.Length + 16];
            var length = _transport!.WriteMessage(plaintext, ciphertext);
            await WriteFrameAsync(ciphertext.AsMemory(0, length), timeout.Token);
        }
        catch (Exception ex)
        {
            Abort(new IOException("Sending to the ESPHome proxy failed. The command will not be retried.", ex));
            throw;
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(frame.AsMemory(3));
        await _stream!.WriteAsync(frame, cancellationToken);
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[3];
        await _stream!.ReadExactlyAsync(header, cancellationToken);
        if (header[0] == 0)
            throw new ESPHomeConnectionException("The relay is using the unencrypted ESPHome API. Enable api: encryption in its firmware.");
        if (header[0] != 1)
            throw new ESPHomeConnectionException("Something other than the ESPHome native API answered on this port. Check the port.");
        var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1))];
        await _stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_lifetime.Token);
                if (frame.Length < 20)
                    throw new IOException("ESPHome sent a truncated encrypted message.");
                var plaintext = new byte[frame.Length - 16];
                var length = _transport!.ReadMessage(frame, plaintext);
                if (length < 4 || BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(2)) != length - 4)
                    throw new IOException("ESPHome sent an invalid message length.");
                var packet = new ESPHomePacket((ApiMessageType)BinaryPrimitives.ReadUInt16BigEndian(plaintext), plaintext[4..length]);
                if (packet.Type == ApiMessageType.PingRequest)
                    await SendAsync(ApiMessageType.PingResponse, new PingResponse(), _lifetime.Token);
                else if (packet.Type == ApiMessageType.DisconnectRequest)
                {
                    await SendAsync(ApiMessageType.DisconnectResponse, new DisconnectResponse(), _lifetime.Token);
                    Abort(new IOException("The ESPHome device closed the connection."));
                    return;
                }
                else
                    PacketReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Abort(new IOException("The ESPHome proxy connection was lost.", ex)); }
    }

    private async Task KeepAliveLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await RequestAsync(ApiMessageType.PingRequest, new PingRequest(), ApiMessageType.PingResponse,
                    PingResponse.Parser, cancellationToken: _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Abort(new IOException("The ESPHome proxy stopped responding.", ex)); }
    }

    public void Abort(Exception reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _lifetime.Cancel();
        _tcp.Dispose();
        ConnectionClosed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        Abort(new IOException("The ESPHome proxy connection was closed."));
        if (_receiveLoop != null)
            await _receiveLoop;
        if (_keepAliveLoop != null)
            await _keepAliveLoop;
        await _writeLock.WaitAsync();
        try { _transport?.Dispose(); }
        finally { _writeLock.Release(); }
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
