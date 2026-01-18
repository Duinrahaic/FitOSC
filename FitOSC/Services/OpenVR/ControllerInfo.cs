namespace FitOSC.Services.OpenVR;

/// <summary>
/// Information about connected VR controllers
/// </summary>
public class ControllerInfo
{
    // Left controller
    public bool LeftConnected { get; set; } = false;
    public string LeftControllerType { get; set; } = string.Empty;
    public string LeftModelNumber { get; set; } = string.Empty;
    public string LeftRenderModel { get; set; } = string.Empty;

    // Right controller
    public bool RightConnected { get; set; } = false;
    public string RightControllerType { get; set; } = string.Empty;
    public string RightModelNumber { get; set; } = string.Empty;
    public string RightRenderModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets a friendly display name for the controller type
    /// </summary>
    public string GetControllerDisplayName()
    {
        var type = !string.IsNullOrEmpty(LeftControllerType) ? LeftControllerType : RightControllerType;

        return type.ToLowerInvariant() switch
        {
            "knuckles" => "Valve Index Controllers",
            "vive_controller" => "HTC Vive Controllers",
            "oculus_touch" => "Oculus Touch Controllers",
            "holographic_controller" => "Windows Mixed Reality Controllers",
            "vive_cosmos_controller" => "HTC Vive Cosmos Controllers",
            "vive_tracker" => "HTC Vive Tracker",
            _ when type.Contains("quest", StringComparison.OrdinalIgnoreCase) => "Meta Quest Controllers",
            _ when type.Contains("pico", StringComparison.OrdinalIgnoreCase) => "Pico Controllers",
            _ when !string.IsNullOrEmpty(type) => type,
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets whether this controller type uses thumbsticks (vs trackpads)
    /// </summary>
    public bool HasThumbsticks()
    {
        var type = !string.IsNullOrEmpty(LeftControllerType) ? LeftControllerType : RightControllerType;

        return type.ToLowerInvariant() switch
        {
            "vive_controller" => false, // Vive wands use trackpads
            _ => true // Most modern controllers use thumbsticks
        };
    }

    /// <summary>
    /// Gets the appropriate input label based on controller type
    /// </summary>
    public string GetInputLabel()
    {
        return HasThumbsticks() ? "Thumbstick" : "Trackpad";
    }
}
