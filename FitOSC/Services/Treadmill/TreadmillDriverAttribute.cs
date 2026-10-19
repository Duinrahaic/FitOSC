namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TreadmillDriverAttribute(TreadmillType type, string name, string description) : Attribute
{
    public TreadmillType Type { get; } = type;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool Experimental { get; set; }

    /// <summary>Advertised BLE service UUIDs, any of which suggests this driver in the debug scan.</summary>
    public string[] AdvertisedServices { get; set; } = [];
}
