using FitOSC.Models;
using SkiaSharp;

namespace FitOSC.Overlay;

public sealed record HudView(string Speed, string Unit, string Treadmill, string Mode,
    string WalkingPercent, string HeartRate, ConnectionStatus Bluetooth, ConnectionStatus Osc);

public static class HudPanel
{
    public static void Draw(SKCanvas canvas, HudView view, SKTypeface typeface)
    {
        canvas.Clear(new SKColor(18, 23, 32));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(48, 58, 72) };
        canvas.DrawLine(176, 12, 176, 116, paint);
        canvas.DrawLine(352, 12, 352, 116, paint);
        Text(view.Speed, 16, 53, 40);
        Text(view.Unit, 16, 78, 15);
        Text(view.Treadmill, 16, 104, 11);
        Text(view.Mode, 192, 35, 18);
        Text(view.WalkingPercent, 192, 82, 36);
        Text(view.HeartRate, 368, 42, 24);
        Dot(view.Bluetooth, 378, 78, "BT");
        Dot(view.Osc, 378, 106, "OSC");

        void Text(string text, float x, float y, float size)
        {
            using var font = new SKFont(typeface, size);
            paint.Color = new SKColor(232, 238, 246);
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
        }
        void Dot(ConnectionStatus status, float x, float y, string label)
        {
            paint.Color = status switch
            {
                ConnectionStatus.Connected => new SKColor(66, 211, 146),
                ConnectionStatus.Connecting => new SKColor(245, 190, 65),
                ConnectionStatus.Error => new SKColor(239, 85, 92),
                ConnectionStatus.Disconnected => new SKColor(105, 115, 130),
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
            canvas.DrawCircle(x, y - 5, 5, paint);
            Text(label, x + 14, y, 14);
        }
    }
}
