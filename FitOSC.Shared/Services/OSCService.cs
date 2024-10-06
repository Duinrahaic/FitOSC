using CoreOSC;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Blazored.LocalStorage;
using FitOSC.Shared.Config;

namespace FitOSC.Shared.Services;

public class OscService : IDisposable
{
    public delegate void OscSubscriptionEventHandler(OscSubscriptionEvent e);
    public event OscSubscriptionEventHandler? OnOscMessageReceived;

    private readonly ILogger<OscService> _logger;
    private UDPListener? _listener;
    private UDPSender? _sender;
    private CancellationTokenSource? _cts;
    private ConfigService _configService;

    private FitOscConfig _config = new FitOscConfig();
    public OscService(IServiceProvider services) 
    {
        _logger = services.GetService<ILogger<OscService>>();
        _configService = services.GetService<ConfigService>();
        _logger.LogInformation("Initialized OSCService");
    }

    public async Task RestartService()
    {
        await Stop();
        await Start();
    }

    public async Task Stop()
    {
        _sender?.Close();
        _listener?.Close();
        _listener?.Dispose();
    }
 
    public async Task Start()
    {
        if(_sender != null || _listener != null)
        {
            _logger.LogWarning("OSC Service already started");
            return;
        }
        

        try
        {
            _config = await _configService.GetConfig();

            _sender = new UDPSender("127.0.0.1", _config.OscSenderPort);
            _listener = new UDPListener(_config.OscListenerPort, (HandleOscPacket)Callback);
            _logger.LogInformation($"OSC Initialized: Listening on {_config.OscListenerPort} and sending on {_config.OscSenderPort}");

        }
        catch(Exception ex)
        {
            _logger.LogError($"OSC Failed: {ex.Message}");
 
        }
    }
    
    void Callback(OscPacket packet)
    {
        var messageReceived = (OscMessage)packet;
        OnOscMessageReceived?.Invoke(new OscSubscriptionEvent(messageReceived));
    }

    public void SetWalkingSpeed(decimal speed)
    {
        SendMessage("/input/Run", speed != 0m);
        float vertical = (float)Math.Clamp(speed,-1,1);
        SendMessage("/input/Vertical", vertical);
    }
    
    public void SetWakingState(bool walking)
    {
        SendMessage("/avatar/parameters/TMC_Walk", walking);
    }
 
    public void SendMessage(string address, params object?[]? args)
    {
        
        
        if (string.IsNullOrEmpty(address))
            throw new NullReferenceException("address cannot be null or empty");
        if (args == null)
            return;
        var message = new OscMessage(address, args);
        try
        {
            _sender?.Send(message);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error occurred while sending OSC message: {ex}");
        }
    }
    
 

    public void Dispose()
    {
        _sender?.Close();
        _listener?.Close();
        _listener?.Dispose();
    }
}