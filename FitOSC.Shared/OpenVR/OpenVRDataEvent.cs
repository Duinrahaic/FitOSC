namespace Valve.VR;

public class OpenVRDataEvent
{
    public OpenVRTurn Turn { get; set; } = OpenVRTurn.None;
    public float VerticalAdjustment { get; set; } = 0;
    public float HorizontalAdjustment { get; set; } = 0;
}