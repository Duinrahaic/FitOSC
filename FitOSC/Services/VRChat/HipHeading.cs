using System.Numerics;

namespace FitOSC.Services.VRChat;

/// <summary>
/// Tracks which way the hips face, in the same yaw convention as the headset.
/// Trackers are strapped on at arbitrary angles, so calibration records which tracker-local
/// direction matches the head's forward; afterwards that direction is followed as the hips turn.
/// </summary>
internal sealed class HipHeading
{
    private readonly object _lock = new();
    private Vector3? _localForward;
    private float? _yaw;

    /// <summary>
    /// Latest hip yaw in radians, or null when not calibrated. Holds the last value while the tracker is lost.
    /// </summary>
    public float? Yaw
    {
        get { lock (_lock) return _yaw; }
    }

    /// <summary>
    /// Treats the head's current facing as forward for the hips.
    /// </summary>
    public void Calibrate(float headYaw, Quaternion hipRotation)
    {
        var forward = new Vector3(-MathF.Sin(headYaw), 0f, -MathF.Cos(headYaw));
        lock (_lock)
        {
            _localForward = Vector3.Transform(forward, Quaternion.Inverse(hipRotation));
            _yaw = headYaw;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _localForward = null;
            _yaw = null;
        }
    }

    public void Update(Quaternion? hipRotation)
    {
        if (hipRotation is not { } rotation)
            return;

        lock (_lock)
        {
            if (_localForward is not { } local)
                return;

            var forward = Vector3.Transform(local, rotation);

            // Forward pointing almost straight up or down has no usable heading
            if (forward.X * forward.X + forward.Z * forward.Z < 0.01f)
                return;

            _yaw = MathF.Atan2(-forward.X, -forward.Z);
        }
    }
}
