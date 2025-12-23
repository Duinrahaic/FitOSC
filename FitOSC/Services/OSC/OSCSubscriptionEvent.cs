using LucHeart.CoreOSC;

namespace FitOSC.Services.OSC;

public class OscSubscriptionEvent(OscMessage message) : EventArgs
{
    public OscMessage Message { get; private set; } = message;
}