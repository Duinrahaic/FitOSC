using System.Net;
using LucHeart.CoreOSC;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
 using FitOSC.Shared.Config;
using OscQueryLibrary;
using OscQueryLibrary.Utils;

namespace FitOSC.Shared.Services;

public class OscService : IDisposable
{
    public delegate void OscSubscriptionEventHandler(OscSubscriptionEvent e);
    public event OscSubscriptionEventHandler? OnOscMessageReceived;

    private readonly ILogger<OscService> _logger;
    private readonly CancellationTokenSource? _cts;
    private OscQueryServer? _server;
    public OscService(ILogger<OscService> logger) 
    {
        _logger = logger;
        _cts = new CancellationTokenSource();
        _logger.LogInformation("Initialized OSCService");

    }

    public void RestartService()
    {      
        Stop();
        Start();
    }

    public void Stop()
    {
        try
        {
            _server?.Dispose();
            _server = null;
            _connection?.Dispose();
            _connection = null;
        }catch(Exception ex)
        {
            _logger.LogError($"Error stopping OSC: {ex.Message}");
        }
    }
 
    public void Start()
    {
        if(_connection != null)
        {
            _logger.LogWarning("OSC Service already started");
            return;
        }
        

        try
        {
            if (OperatingSystem.IsBrowser())
            {
                return;
            }
            
            
            _server = new OscQueryServer("FitOSC", IPAddress.Loopback);
            _server.FoundVrcClient += FoundVrcClient; // event on vrc discovery
            _server.Start();
            _logger.LogInformation($"OSC Query Server Started");

        }
        catch(Exception ex)
        {
            _logger.LogError($"OSC Failed: {ex.Message}");
 
        }
    }
    private CancellationTokenSource _loopCancellationToken = new CancellationTokenSource();
    private OscQueryServer? _currentOscQueryServer = null;
    private OscDuplex? _connection = null;

    private Task FoundVrcClient(OscQueryServer oscQueryServer, IPEndPoint ipEndPoint)
    {
        _loopCancellationToken.Cancel();
        _loopCancellationToken = new CancellationTokenSource();
        _connection?.Dispose();
        _connection = null;

        _logger.LogInformation("Found VRC client at {EndPoint}", ipEndPoint);
        _logger.LogInformation("Starting listening for VRC client at {Port}", oscQueryServer.OscReceivePort);
        _connection = new OscDuplex(new IPEndPoint(ipEndPoint.Address, oscQueryServer.OscReceivePort), ipEndPoint);
        _currentOscQueryServer = oscQueryServer;
        
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();
        ErrorHandledTask.Run(ReceiverLoopAsync);
        return Task.CompletedTask;
    }
    
    private void Cleanup()
    {
        _currentOscQueryServer?.Dispose();
        _currentOscQueryServer = null;
        _connection?.Dispose();
        _connection = null;
    }
    
    private async Task ReceiverLoopAsync()
    {
        var currentCancellationToken = _loopCancellationToken.Token;
        while (!currentCancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_connection != null)
                {
                     
                    OscMessage received = await _connection.ReceiveMessageAsync();
                    if(received.Address.Contains("TMC"))
                    {
                        var message = received;
                        _ = Task.Run(() => OnOscMessageReceived?.Invoke(new OscSubscriptionEvent(message)), currentCancellationToken);
                    }
                }
                
                
            }
            catch (Exception e)
            {
                _logger.LogError("Error in receiver loop", e);
            }
        }
    }
    
    private float currentVertical = 0;
    public void SetWalkingSpeed(decimal speed)
    {
        float vertical = (float)Math.Clamp(speed,-1,1);
        if(vertical == currentVertical)
            return;
        currentVertical = vertical;
        SendMessage("/input/Vertical", currentVertical);
    }
    
    private float currentHorizontal = 0;
    public void SetHorizontalSpeed(decimal speed)
    {
        float horizontal = (float)Math.Clamp(speed,-1,1);
        if(horizontal == currentHorizontal)
            return;
        currentHorizontal = horizontal;
        SendMessage("/input/Horizontal", currentHorizontal);
    }
    
    private float currentTurn = 0;
    public void SetTurningSpeed(float speed)
    {
        float turn = (float)Math.Clamp(speed,-1.0,1.0);
        if(turn == currentTurn)
            return;
        currentTurn = turn;
        SendMessage("/input/LookHorizontal", currentTurn);
    }
    
    public void SetWakingState(bool walking)
    {
        SendMessage("/avatar/parameters/TMC_Walk", walking);
    }
 
    private void SendMessage(string address, params object?[]? args)
    {
        if(OperatingSystem.IsBrowser())
            return;
        if (string.IsNullOrEmpty(address))
            throw new NullReferenceException("address cannot be null or empty");
        if (args == null)
            return;
        if(_connection == null)
            return;
        
        var message = new OscMessage(address, args);
        try
        {
            _connection?.SendAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error occurred while sending OSC message: {ex}");
        }
    }

    private void ReleaseUnmanagedResources()
    {
        if (_server != null)
        {
            _server.FoundVrcClient -= FoundVrcClient; // event on vrc discovery
        }

    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            _cts?.Dispose();
            _server?.Dispose();
            _loopCancellationToken.Dispose();
            _currentOscQueryServer?.Dispose();
            _connection?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~OscService()
    {
        Dispose(false);
    }
}