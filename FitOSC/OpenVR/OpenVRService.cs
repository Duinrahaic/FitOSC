using System.Numerics;

// ReSharper disable once CheckNamespace
namespace Valve.VR;

public class OpenVRService(IServiceProvider services) : IHostedService, IDisposable
{


    ~OpenVRService()
    {
        Dispose(false);
    }

    private readonly ILogger<OpenVRService>? _logger = services.GetService<ILogger<OpenVRService>>();
    private EVRInitError _initError;
    private bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;
    private static (float yaw, float pitch, float roll)? _initialDirection = null;
    private bool _monitor = false;
    
    // Event for when data update is received
    public delegate void DataUpdateReceivedEventHandler(OpenVRDataEvent e);
    public event DataUpdateReceivedEventHandler? OnDataUpdateReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OpenVR.Init(ref _initError, EVRApplicationType.VRApplication_Background);

        if (_initError != EVRInitError.None)
        {
            _logger?.LogError($"Failed to initialize OpenVR: {_initError}");
            return;
        }

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



    public void StartMonitoring() => _monitor = true;
    public void StopMonitoring() => _monitor = false;

    private async Task PollVrEvents(CancellationToken cancellationToken)
    {
        TrackedDevicePose_t[] poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var vrSystem = OpenVR.System;
        HmdMatrix34_t poseMatrix;
        Quaternion quaternion;
        (float yaw, float pitch, float roll) euler;
        (float X, float Y, float Z) position = (0, 0, 0);

        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, poses);

            if (vrSystem.IsTrackedDeviceConnected((uint)OpenVR.k_unTrackedDeviceIndex_Hmd) &&
                poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid)
            {
                if (!_monitor)
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

                    float yawDifference = NormalizeAngleDifference(euler.yaw - _initialDirection.Value.yaw);
                    float verticalInput = 1f;
                    float horizontalInput = Math.Clamp(yawDifference, -1f, 1f);

                    OpenVRTurn turnCmd = euler.roll switch
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

        float yaw = (float)Math.Atan2(2.0f * (q.Y * q.W + q.X * q.Z), 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y));
        float sinp = 2.0f * (q.W * q.X - q.Z * q.Y);
        float pitch = Math.Abs(sinp) >= 1 ? (float)Math.CopySign(Math.PI / 2, sinp) : (float)Math.Asin(sinp);
        float roll = (float)Math.Atan2(2.0f * (q.W * q.Z + q.X * q.Y), 1.0f - 2.0f * (q.X * q.X + q.Z * q.Z));

        return (yaw, pitch, roll);
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float length = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    private static Quaternion GetRotationFromMatrix(HmdMatrix34_t matrix)
    {
        float w = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 + matrix.m5 + matrix.m10)) / 2;
        float x = (float)Math.Sqrt(Math.Max(0, 1 + matrix.m0 - matrix.m5 - matrix.m10)) / 2;
        float y = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 + matrix.m5 - matrix.m10)) / 2;
        float z = (float)Math.Sqrt(Math.Max(0, 1 - matrix.m0 - matrix.m5 + matrix.m10)) / 2;
        x = (float)Math.CopySign(x, matrix.m9 - matrix.m6);
        y = (float)Math.CopySign(y, matrix.m2 - matrix.m8);
        z = (float)Math.CopySign(z, matrix.m4 - matrix.m1);

        return new Quaternion(x, y, z, w);
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

    private void ReleaseUnmanagedResources()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = null;
        OpenVR.Shutdown();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            _cancellationTokenSource?.Dispose();
        }
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
