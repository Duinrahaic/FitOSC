using System.Runtime.InteropServices;

namespace FitOSC.Platform.Windows;

public static class FatalErrorDialog
{
    public static void Show(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "FitOSC Fatal Error", 0x10);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
