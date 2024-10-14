using System;
using System.Collections.Generic;
using LucHeart.CoreOSC;


namespace FitOSC.Shared.Services;

public class OscSubscriptionEvent(OscMessage message) : EventArgs
{
    public  OscMessage Message { get; private set; } = message;
    
}