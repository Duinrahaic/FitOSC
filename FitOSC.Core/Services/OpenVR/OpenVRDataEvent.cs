namespace FitOSC.Services.OpenVR;

public class OpenVRDataEvent
{
    public OpenVRTurn Turn { get; set; } = OpenVRTurn.None;
    public float VerticalAdjustment { get; set; } = 0;
    public float HorizontalAdjustment { get; set; } = 0;

    // Raw headset tracking data
    public float Yaw { get; set; } = 0;
    public float Pitch { get; set; } = 0;
    public float Roll { get; set; } = 0;
    public float PositionX { get; set; } = 0;
    public float PositionY { get; set; } = 0;
    public float PositionZ { get; set; } = 0;

    // Right controller thumbstick Y axis (-1 = down, +1 = up)
    public float RightThumbstickY { get; set; } = 0;

    // Left controller thumbstick axes for manual movement detection
    public float LeftThumbstickX { get; set; } = 0;
    public float LeftThumbstickY { get; set; } = 0;
}