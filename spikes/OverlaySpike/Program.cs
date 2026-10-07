// Phase 0 overlay spike: proves a Skia-drawn panel can be shown as a SteamVR overlay
// and receive laser-pointer clicks, on both Windows and Linux.
//
//   dotnet run -- wrist   attach to the left controller (click it with the right-hand laser)
//   dotnet run -- hud     lock to the headset 1 m ahead (display check only)

using System.Runtime.InteropServices;
using SkiaSharp;
using Valve.VR;

const int Width = 512;
const int Height = 256;
var mode = args.FirstOrDefault() ?? "wrist";

var initError = EVRInitError.None;
OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
if (initError != EVRInitError.None)
{
    Console.WriteLine($"OpenVR init failed: {initError}");
    return 1;
}

ulong handle = 0;
Check(OpenVR.Overlay.CreateOverlay("fitosc.spike.panel", "FitOSC overlay spike", ref handle), "CreateOverlay");
Check(OpenVR.Overlay.SetOverlayWidthInMeters(handle, mode == "hud" ? 0.4f : 0.15f), "SetOverlayWidthInMeters");
Check(OpenVR.Overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.Mouse), "SetOverlayInputMethod");
var mouseScale = new HmdVector2_t { v0 = Width, v1 = Height };
Check(OpenVR.Overlay.SetOverlayMouseScale(handle, ref mouseScale), "SetOverlayMouseScale");
Check(OpenVR.Overlay.SetOverlayFlag(handle, VROverlayFlags.MakeOverlaysInteractiveIfVisible, true), "SetOverlayFlag");

if (mode == "hud")
{
    var transform = Translation(0f, -0.1f, -1.0f);
    Check(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform), "Attach to HMD");
}
else
{
    var leftHand = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
    if (leftHand == OpenVR.k_unTrackedDeviceIndexInvalid)
    {
        Console.WriteLine("Left controller not found. Turn it on and restart the spike.");
        return 1;
    }

    var transform = Translation(0f, 0.05f, 0.1f);
    Check(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(handle, leftHand, ref transform), "Attach to left controller");
}

Check(OpenVR.Overlay.ShowOverlay(handle), "ShowOverlay");

var button = new SKRect(32, 150, 256, 230);
var clicks = 0;
var frames = 0;
var uploadTicks = 0L;
var running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };
Console.WriteLine($"Overlay visible ({mode}). Point the laser at it and click the blue button. Ctrl+C to quit.");

using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
var vrEvent = new VREvent_t();
var eventSize = (uint)Marshal.SizeOf<VREvent_t>();

while (running)
{
    while (OpenVR.Overlay.PollNextOverlayEvent(handle, ref vrEvent, eventSize))
    {
        if (vrEvent.eventType != (uint)EVREventType.VREvent_MouseButtonDown) continue;

        // Overlay mouse coordinates start at the bottom-left corner; Skia starts at the top-left.
        var x = vrEvent.data.mouse.x;
        var y = Height - vrEvent.data.mouse.y;
        var hit = button.Contains(x, y);
        if (hit) clicks++;
        Console.WriteLine($"Click raw=({vrEvent.data.mouse.x:F0},{vrEvent.data.mouse.y:F0}) flipped=({x:F0},{y:F0}) hit={hit}");
    }

    Draw(surface.Canvas, frames, clicks);
    using var pixels = surface.PeekPixels();
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    Check(OpenVR.Overlay.SetOverlayRaw(handle, pixels.GetPixels(), Width, Height, 4), "SetOverlayRaw");
    uploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
    frames++;

    if (frames % 100 == 0)
    {
        var averageMs = uploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
        Console.WriteLine($"{frames} frames, average SetOverlayRaw {averageMs:F2} ms");
    }

    Thread.Sleep(100);
}

OpenVR.Overlay.DestroyOverlay(handle);
OpenVR.Shutdown();
return 0;

void Draw(SKCanvas canvas, int frame, int clickCount)
{
    canvas.Clear(new SKColor(20, 20, 28, 220));
    using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
    using var font = new SKFont(SKTypeface.Default, 30);
    canvas.DrawText("FitOSC overlay spike", 32, 48, SKTextAlign.Left, font, text);
    canvas.DrawText($"frame {frame}   clicks {clickCount}", 32, 92, SKTextAlign.Left, font, text);

    // Channel-order check: these must appear red, green and blue in the headset.
    using var swatch = new SKPaint();
    swatch.Color = SKColors.Red; canvas.DrawRect(300, 150, 60, 30, swatch);
    swatch.Color = SKColors.Lime; canvas.DrawRect(370, 150, 60, 30, swatch);
    swatch.Color = SKColors.Blue; canvas.DrawRect(440, 150, 60, 30, swatch);

    using var buttonPaint = new SKPaint { Color = new SKColor(33, 150, 243), IsAntialias = true };
    canvas.DrawRoundRect(button, 12, 12, buttonPaint);
    canvas.DrawText("Click me", button.MidX, button.MidY + 10, SKTextAlign.Center, font, text);
}

static HmdMatrix34_t Translation(float x, float y, float z) => new()
{
    m0 = 1, m1 = 0, m2 = 0, m3 = x,
    m4 = 0, m5 = 1, m6 = 0, m7 = y,
    m8 = 0, m9 = 0, m10 = 1, m11 = z
};

static void Check(EVROverlayError error, string step)
{
    if (error != EVROverlayError.None)
        Console.WriteLine($"{step} failed: {error}");
}
