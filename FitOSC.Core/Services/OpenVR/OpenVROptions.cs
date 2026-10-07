namespace FitOSC.Services.OpenVR;

public sealed record OpenVROptions
{
    public required string ApplicationManifestPath { get; init; }
    public bool Disabled { get; init; }
}
