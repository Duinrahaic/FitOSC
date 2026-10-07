using FitOSC.Models;
using SkiaSharp;

namespace FitOSC.Overlay;

public enum WristButton { None, Walk, Recenter, Dynamic, Override, Decrease, Increase }

public sealed record WristView(WalkingMode Mode, bool Manual, string WalkingPercent, string HeartRate,
    string ValueText, bool CanDecrease, bool CanIncrease, WristButton Hover, WristButton Pressed);

public static class WristPanel
{
    private static readonly (WristButton Button, SKRect Bounds, string Label)[] Buttons =
    [
        (WristButton.Walk, new(8, 64, 124, 152), "WALK"),
        (WristButton.Recenter, new(132, 64, 248, 152), "RECENTER"),
        (WristButton.Dynamic, new(256, 64, 372, 152), "DYNAMIC"),
        (WristButton.Override, new(380, 64, 504, 152), "OVERRIDE"),
        (WristButton.Decrease, new(8, 160, 124, 248), "-"),
        (WristButton.Increase, new(380, 160, 504, 248), "+")
    ];

    public static WristButton HitTest(float px, float py)
    {
        foreach (var (button, rect, _) in Buttons)
            if (rect.Contains(px, py)) return button;
        return WristButton.None;
    }

    public static void Draw(SKCanvas canvas, WristView view, SKTypeface typeface)
    {
        canvas.Clear(new SKColor(18, 23, 32));
        using var paint = new SKPaint { IsAntialias = true };
        var mode = view.Mode switch
        {
            WalkingMode.Disabled => "WALK OFF",
            WalkingMode.Dynamic => "DYNAMIC",
            WalkingMode.Override => "OVERRIDE",
            _ => throw new ArgumentOutOfRangeException(nameof(view.Mode))
        };
        Text(view.Manual ? "MANUAL" : mode, 16, 35, 22);
        Text(view.WalkingPercent, 276, 35, 22);
        Text(view.HeartRate, 396, 35, 20);
        foreach (var (button, rect, label) in Buttons)
        {
            var enabled = button != WristButton.Decrease && button != WristButton.Increase ||
                (button == WristButton.Decrease ? view.CanDecrease : view.CanIncrease);
            var lit = button switch
            {
                WristButton.Walk => view.Mode != WalkingMode.Disabled,
                WristButton.Dynamic => view.Mode == WalkingMode.Dynamic,
                WristButton.Override => view.Mode == WalkingMode.Override,
                _ => false
            };
            paint.Color = !enabled ? new SKColor(28, 34, 44) :
                view.Pressed == button ? new SKColor(43, 130, 161) :
                view.Hover == button ? new SKColor(70, 95, 122) :
                lit ? new SKColor(31, 100, 91) : new SKColor(39, 50, 66);
            canvas.DrawRoundRect(rect, 8, 8, paint);
            Text(label, rect.MidX, rect.MidY + 7,
                button is WristButton.Decrease or WristButton.Increase ? 38 : 18, true, enabled);
        }
        Text(view.ValueText, 252, 211, 22, true);

        void Text(string text, float x, float y, float size, bool center = false, bool enabled = true)
        {
            using var font = new SKFont(typeface, size);
            paint.Color = enabled ? new SKColor(232, 238, 246) : new SKColor(96, 106, 120);
            canvas.DrawText(text, x, y, center ? SKTextAlign.Center : SKTextAlign.Left, font, paint);
        }
    }
}
