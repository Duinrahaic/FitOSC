using System.Runtime.InteropServices;

namespace FitOSC.Platform.Linux;

public static class DesktopIdentity
{
    public static void Initialize() => g_set_prgname("FitOSC");

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
