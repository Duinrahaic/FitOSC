#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Photino.NET;

namespace FitOSC;

internal static class WindowsWindowSizing
{
    // Photino's Windows size includes decorations; GTK's size is the content area.
    public static void SetContentSize(PhotinoWindow window, int width, int height)
    {
        var handle = window.WindowHandle;
        if (!GetWindowRect(handle, out var outer) || !GetClientRect(handle, out var client))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var dpi = GetDpiForWindow(handle);
        if (dpi == 0)
            throw new InvalidOperationException("Cannot determine the FitOSC window DPI.");

        var contentWidth = (int)Math.Round(width * dpi / 96.0);
        var contentHeight = (int)Math.Round(height * dpi / 96.0);
        window.SetSize(
            contentWidth + outer.Right - outer.Left - (client.Right - client.Left),
            contentHeight + outer.Bottom - outer.Top - (client.Bottom - client.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
#endif
