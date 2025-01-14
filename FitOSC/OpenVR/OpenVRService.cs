using System.Numerics;

// ReSharper disable once CheckNamespace
namespace Valve.VR;

public class OpenVRService(IServiceProvider services) : IHostedService, IDisposable
{
    // Event for when data update is received
    public delegate void DataUpdateReceivedEventHandler(OpenVRDataEvent e);

    private static (float yaw, float pitch, float roll)? _initialDirection;

    private readonly ILogger<OpenVRService>? _logger = services.GetService<ILogger<OpenVRService>>();
    private CancellationTokenSource? _cancellationTokenSource;
    private EVRInitError _initError;
    private bool _isRunning;
    public bool IsMonitoring { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        

        _logger?.LogInformation("OpenVR initialized successfully.");
        _isRunning = true;

        try
        {
            Task.Run(() => PollVrEvents(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("VR event polling was canceled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"An error occurred in VR event polling: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _isRunning = false;

        // Signal the cancellation of the VR event polling task
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
        OpenVR.Shutdown();
        // Shutdown OpenVR when the service stops
        return Task.CompletedTask;
    }


    ~OpenVRService()
    {
        Dispose(false);
    }

    public event DataUpdateReceivedEventHandler? OnDataUpdateReceived;


    private bool TryInitialize()
    {
        OpenVR.Shutdown();
        OpenVR.Init(ref _initError, EVRApplicationType.VRApplication_Background);
        if (_initError != EVRInitError.None)
        {
            _logger?.LogError($"Failed to initialize OpenVR: {_initError}");
            return false;
        }

        return true;
    }
    
    public void StartMonitoring()
    {
        bool success = TryInitialize();
        IsMonitoring = success;
    }

    public void StopMonitoring()
    {
        IsMonitoring = false;
    }

    private async Task PollVrEvents(CancellationToken cancellationToken)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var vrSystem = OpenVR.System;
        HmdMatrix34_t poseMatrix;
        Quaternion quaternion;
        (float yaw, float pitch, float roll) euler;
        (float X, float Y, float Z) position = (0, 0, 0);

        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

            if (vrSystem.IsTrackedDeviceConnected(OpenVR.k_unTrackedDeviceIndex_Hmd) &&
                poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid)
            {
                if (!IsMonitoring)
                {
                    _initialDirection = null;
                }
                else
                {
                    poseMatrix = poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking;
                    position = (poseMatrix.m3, poseMatrix.m7, poseMatrix.m11);
                    quaternion = GetRotationFromMatrix(poseMatrix);
                    euler = QuaternionToEuler(quaternion);

                    if (_initialDirection == null)
                    {
                        _initialDirection = euler;
                        _logger?.LogInformation($"Initial Direction: {_initialDirection}");
                    }

                    var yawDifference = NormalizeAngleDifference(euler.yaw - _initialDirection.Value.yaw);
                    var verticalInput = 1f;
                    var horizontalInput = Math.Clamp(yawDifference, -1f, 1f);

                    var turnCmd = euler.roll switch
                    {
                        > 0.6f => OpenVRTurn.Left,
                        < -0.6f => OpenVRTurn.Right,
                        _ => OpenVRTurn.None
                    };

                    Task.Run(() =>
                    {
                        OnDataUpdateReceived?.Invoke(new OpenVRDataEvent
                        {
                            Turn = turnCmd,
                            VerticalAdjustment = verticalInput,
                            HorizontalAdjustment = horizontalInput
                        });
                    }).ConfigureAwait(false);
                }
            }

            // Non-blocking delay with cancellation support
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static float NormalizeAngleDifference(float angle)
    {
        while (angle > Math.PI) angle -= 2 * (float)Math.PI;
        while (angle < -Math.PI) angle += 2 * (float)Math.PI;
        return angle;
    }

    private static (float yaw, float pitch, float roll) QuaternionToEuler(Quaternion q)
    {
        q = NormalizeQuaternion(q);

        var yaw = (float)Math.Atan2(2.0f * (q.Y * q.W + q.X * q.Z), 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y));
        var sinp = 2.0f * (q.W * q.X - q.Z * q.Y);
        var pitch = Math.Abs(sinp) >= 1 ? (float)Math.CopySign(Math.PI / 2, sinp) : (float)Math.Asin(sinp);
        var roll = (float)Math.Atan2(2.0f * (q.W * q.Z + q.X * q.Y), 1.0f - 2.0f * (q.X * q.X + q.Z * q.Z));

        return (yaw, pitch, roll);
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        var length = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    private static Quaternion GetRotationFromMatrix(HmdMatrix34_t matrix)
    {
        var w = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 + matrix.m5 + matrix.m10)) / 2;
        var x = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 - matrix.m5 - matrix.m10)) / 2;
        var y = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 + matrix.m5 - matrix.m10)) / 2;
        var z = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 - matrix.m5 + matrix.m10)) / 2;
        x = (float)Math.CopySign(x, matrix.m9 - matrix.m6);
        y = (float)Math.CopySign(y, matrix.m2 - matrix.m8);
        z = (float)Math.CopySign(z, matrix.m4 - matrix.m1);

        return new Quaternion(x, y, z, w);
    }

    private void ReleaseUnmanagedResources()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
        OpenVR.Shutdown();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing) _cancellationTokenSource?.Dispose();
    }
}