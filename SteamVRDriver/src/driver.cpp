// FitOSC SteamVR driver: blends FitOSC walking movement into the left controller's thumbstick.
//
// SteamVR has no API for one driver to change another driver's input, so this driver hooks
// IVRDriverInput inside vrserver (the same technique as OpenVR-SpaceCalibrator). It loads before the
// controller drivers (see loadPriority in default.vrsettings) so it sees their thumbstick components
// being created, then rewrites the left-hand thumbstick axes while FitOSC is sending movement.
// The user's own stick always wins: pushing it past the deadzone passes the real values through.
//
// FitOSC sends MovePacket datagrams to 127.0.0.1:kPort and gets a StatusPacket back for each one.
// If no packet arrives for kInjectionTimeout the real stick values are restored.

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <MinHook.h>
#include <openvr_driver.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace
{
    // Keep in sync with SteamVRJoystickService in the FitOSC app.
    constexpr uint32_t kMagic = 0x43534F46; // "FOSC"
    constexpr uint16_t kProtocolVersion = 1;
    constexpr uint16_t kPort = 39574;

    constexpr auto kInjectionTimeout = std::chrono::milliseconds(250);
    constexpr float kUserDeadzone = 0.2f;

    // IVRDriverInput_004 vtable slots.
    constexpr int kCreateScalarSlot = 2;
    constexpr int kUpdateScalarSlot = 3;

#pragma pack(push, 1)
    struct MovePacket
    {
        uint32_t magic;
        uint16_t version;
        uint16_t flags;
        float x;
        float y;
    };

    struct StatusPacket
    {
        uint32_t magic;
        uint16_t version;
        uint16_t flags;
    };
#pragma pack(pop)

    constexpr uint16_t kMoveActive = 1;
    constexpr uint16_t kStatusStickFound = 1;
    constexpr uint16_t kStatusUserOverride = 2;

    using Clock = std::chrono::steady_clock;

    using CreateScalarFn = vr::EVRInputError (*)(vr::IVRDriverInput *, vr::PropertyContainerHandle_t, const char *,
                                                 vr::VRInputComponentHandle_t *, vr::EVRScalarType, vr::EVRScalarUnits);
    using UpdateScalarFn = vr::EVRInputError (*)(vr::IVRDriverInput *, vr::VRInputComponentHandle_t, float, double);

    CreateScalarFn g_createScalar = nullptr;
    UpdateScalarFn g_updateScalar = nullptr;

    struct Stick
    {
        // The interface the owning driver created the components through; pushes go through it too.
        vr::IVRDriverInput *owner = nullptr;
        vr::PropertyContainerHandle_t container = vr::k_ulInvalidPropertyContainer;
        vr::VRInputComponentHandle_t axis[2] = {vr::k_ulInvalidInputComponentHandle, vr::k_ulInvalidInputComponentHandle};
        float real[2] = {0.f, 0.f};
    };

    struct Injection
    {
        bool active = false;
        float value[2] = {0.f, 0.f};
        Clock::time_point receivedAt;
    };

    struct AxisRef
    {
        size_t stick;
        int axis;
    };

    std::mutex g_mutex;
    std::vector<Stick> g_sticks;
    std::unordered_map<vr::VRInputComponentHandle_t, AxisRef> g_axes;
    Injection g_injection;

    std::atomic<bool> g_running{false};
    std::thread g_networkThread;
    SOCKET g_socket = INVALID_SOCKET;

    void Log(const std::string &message)
    {
        if (vr::VRDriverLog())
            vr::VRDriverLog()->Log(("[FitOSC] " + message).c_str());
    }

    int AxisFor(const char *name)
    {
        if (std::strcmp(name, "/input/joystick/x") == 0 || std::strcmp(name, "/input/thumbstick/x") == 0)
            return 0;
        if (std::strcmp(name, "/input/joystick/y") == 0 || std::strcmp(name, "/input/thumbstick/y") == 0)
            return 1;
        return -1;
    }

    // Read on every use: some controllers (e.g. Vive wands) swap hands at runtime.
    bool IsLeftHand(const Stick &stick)
    {
        vr::ETrackedPropertyError error = vr::TrackedProp_Success;
        int32_t role = vr::VRProperties()->GetInt32Property(stick.container, vr::Prop_ControllerRoleHint_Int32, &error);
        return error == vr::TrackedProp_Success && role == vr::TrackedControllerRole_LeftHand;
    }

    bool UserActive(const Stick &stick)
    {
        return std::hypot(stick.real[0], stick.real[1]) > kUserDeadzone;
    }

    // Callers hold g_mutex.
    bool InjectionLive(Clock::time_point now)
    {
        return g_injection.active && now - g_injection.receivedAt < kInjectionTimeout;
    }

    // Callers hold g_mutex.
    float OutputFor(const Stick &stick, int axis, Clock::time_point now)
    {
        if (InjectionLive(now) && !UserActive(stick) && IsLeftHand(stick))
            return g_injection.value[axis];
        return stick.real[axis];
    }

    vr::EVRInputError HookCreateScalar(vr::IVRDriverInput *self, vr::PropertyContainerHandle_t container, const char *name,
                                       vr::VRInputComponentHandle_t *handle, vr::EVRScalarType type, vr::EVRScalarUnits units)
    {
        vr::EVRInputError result = g_createScalar(self, container, name, handle, type, units);
        if (result != vr::VRInputError_None || handle == nullptr || name == nullptr)
            return result;

        int axis = AxisFor(name);
        if (axis < 0)
            return result;

        std::lock_guard lock(g_mutex);
        auto it = std::find_if(g_sticks.begin(), g_sticks.end(),
                               [container](const Stick &stick) { return stick.container == container; });
        if (it == g_sticks.end())
        {
            g_sticks.push_back(Stick{});
            it = std::prev(g_sticks.end());
            it->container = container;
        }
        it->owner = self;
        it->axis[axis] = *handle;
        g_axes[*handle] = AxisRef{static_cast<size_t>(it - g_sticks.begin()), axis};
        Log(std::string("Tracking ") + name + " on container " + std::to_string(container));
        return result;
    }

    vr::EVRInputError HookUpdateScalar(vr::IVRDriverInput *self, vr::VRInputComponentHandle_t component, float value,
                                       double timeOffset)
    {
        {
            std::lock_guard lock(g_mutex);
            auto it = g_axes.find(component);
            if (it != g_axes.end())
            {
                Stick &stick = g_sticks[it->second.stick];
                stick.real[it->second.axis] = value;
                value = OutputFor(stick, it->second.axis, Clock::now());
            }
        }
        return g_updateScalar(self, component, value, timeOffset);
    }

    struct Push
    {
        vr::IVRDriverInput *owner;
        vr::VRInputComponentHandle_t component;
        float value;
    };

    // Re-sends every tracked axis, since controllers often only report when the real stick moves.
    void PushAll()
    {
        std::vector<Push> pushes;
        {
            std::lock_guard lock(g_mutex);
            auto now = Clock::now();
            for (const Stick &stick : g_sticks)
                for (int axis = 0; axis < 2; ++axis)
                    if (stick.owner && stick.axis[axis] != vr::k_ulInvalidInputComponentHandle)
                        pushes.push_back(Push{stick.owner, stick.axis[axis], OutputFor(stick, axis, now)});
        }
        for (const Push &push : pushes)
            g_updateScalar(push.owner, push.component, push.value, 0.0);
    }

    uint16_t StatusFlags()
    {
        std::lock_guard lock(g_mutex);
        uint16_t flags = 0;
        for (const Stick &stick : g_sticks)
        {
            if (!IsLeftHand(stick))
                continue;
            flags |= kStatusStickFound;
            if (UserActive(stick))
                flags |= kStatusUserOverride;
        }
        return flags;
    }

    void NetworkLoop()
    {
        bool wasLive = false;
        while (g_running)
        {
            MovePacket packet{};
            sockaddr_in from{};
            int fromLength = sizeof(from);
            int received = recvfrom(g_socket, reinterpret_cast<char *>(&packet), sizeof(packet), 0,
                                    reinterpret_cast<sockaddr *>(&from), &fromLength);

            if (received == sizeof(packet) && packet.magic == kMagic && packet.version == kProtocolVersion)
            {
                {
                    std::lock_guard lock(g_mutex);
                    g_injection.active = (packet.flags & kMoveActive) != 0;
                    g_injection.value[0] = std::clamp(packet.x, -1.f, 1.f);
                    g_injection.value[1] = std::clamp(packet.y, -1.f, 1.f);
                    g_injection.receivedAt = Clock::now();
                    wasLive = g_injection.active;
                }
                PushAll();

                StatusPacket status{kMagic, kProtocolVersion, StatusFlags()};
                sendto(g_socket, reinterpret_cast<const char *>(&status), sizeof(status), 0,
                       reinterpret_cast<const sockaddr *>(&from), fromLength);
                continue;
            }

            // Timed out or ignored a stray datagram: hand the stick back once FitOSC goes quiet.
            bool live;
            {
                std::lock_guard lock(g_mutex);
                live = InjectionLive(Clock::now());
            }
            if (wasLive && !live)
            {
                wasLive = false;
                PushAll();
                Log("FitOSC went quiet; restored the real thumbstick");
            }
        }
    }

    bool StartNetwork()
    {
        WSADATA wsaData;
        if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
        {
            Log("WSAStartup failed");
            return false;
        }

        g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (g_socket == INVALID_SOCKET)
        {
            Log("Could not create the UDP socket");
            WSACleanup();
            return false;
        }

        DWORD timeoutMs = 50;
        setsockopt(g_socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char *>(&timeoutMs), sizeof(timeoutMs));

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_port = htons(kPort);
        inet_pton(AF_INET, "127.0.0.1", &address.sin_addr);
        if (bind(g_socket, reinterpret_cast<const sockaddr *>(&address), sizeof(address)) == SOCKET_ERROR)
        {
            Log("Could not listen on 127.0.0.1:" + std::to_string(kPort) + " (error " + std::to_string(WSAGetLastError()) + ")");
            closesocket(g_socket);
            g_socket = INVALID_SOCKET;
            WSACleanup();
            return false;
        }

        g_running = true;
        g_networkThread = std::thread(NetworkLoop);
        return true;
    }

    void StopNetwork()
    {
        if (!g_running)
            return;
        g_running = false;
        if (g_networkThread.joinable())
            g_networkThread.join();
        closesocket(g_socket);
        g_socket = INVALID_SOCKET;
        WSACleanup();
    }

    bool InstallHooks()
    {
        if (MH_Initialize() != MH_OK)
        {
            Log("MinHook failed to initialize");
            return false;
        }

        void **vtable = *reinterpret_cast<void ***>(vr::VRDriverInput());
        if (MH_CreateHook(vtable[kCreateScalarSlot], reinterpret_cast<void *>(&HookCreateScalar),
                          reinterpret_cast<void **>(&g_createScalar)) != MH_OK ||
            MH_CreateHook(vtable[kUpdateScalarSlot], reinterpret_cast<void *>(&HookUpdateScalar),
                          reinterpret_cast<void **>(&g_updateScalar)) != MH_OK ||
            MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            Log("Could not hook IVRDriverInput");
            MH_Uninitialize();
            return false;
        }
        return true;
    }

    void RemoveHooks()
    {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
    }

    class Provider final : public vr::IServerTrackedDeviceProvider
    {
    public:
        vr::EVRInitError Init(vr::IVRDriverContext *context) override
        {
            VR_INIT_SERVER_DRIVER_CONTEXT(context);

            // Failing here would get the driver blocked by SteamVR, so stay loaded and inert instead.
            m_hooked = InstallHooks();
            if (m_hooked && StartNetwork())
                Log("Ready on 127.0.0.1:" + std::to_string(kPort));
            return vr::VRInitError_None;
        }

        void Cleanup() override
        {
            StopNetwork();
            if (m_hooked)
                RemoveHooks();
            m_hooked = false;
            VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        }

        const char *const *GetInterfaceVersions() override { return vr::k_InterfaceVersions; }
        void RunFrame() override {}
        bool ShouldBlockStandbyMode() override { return false; }
        void EnterStandby() override {}
        void LeaveStandby() override {}

    private:
        bool m_hooked = false;
    };

    Provider g_provider;
}

extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *interfaceName, int *returnCode)
{
    if (std::strcmp(interfaceName, vr::IServerTrackedDeviceProvider_Version) == 0)
        return &g_provider;

    if (returnCode)
        *returnCode = vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
