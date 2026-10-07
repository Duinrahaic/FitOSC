using Valve.VR;

namespace FitOSC.Services.OpenVR;

// Called by the serialized runtime owner between Init and Shutdown.
public interface IOpenVROverlay
{
    void Connect();
    bool Update(TrackedDevicePose_t[] poses, OpenVRActionEvent input);
    void Disconnect();
}
