using FitOSC.Models;

namespace FitOSC.Services.OSC;

public interface IOscService
{
    event EventHandler<OscSubscriptionEvent>? OnOscMessageReceived;
    event EventHandler<OSCServiceConnectionEvent>? OnConnectionStateChanged;
    bool IsConnected { get; }
    int? ListeningPort { get; }
    void RestartService();
    void Stop();
    void Start();
    void SendMessage(string address, params object?[]? args);
    void Dispose();
}