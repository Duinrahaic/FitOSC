using System.Runtime.InteropServices;

namespace FitOSC.Platform.Midi;

internal static class RtMidiNative
{
    // Matches RtMidi 6.0.0's rtmidi_c.h with the platform's default C alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct Wrapper
    {
        public IntPtr Pointer;
        public IntPtr Data;
        public byte Ok; // C bool is one byte.
        public IntPtr Message;
    }

    private static readonly int OkOffset = Marshal.OffsetOf<Wrapper>(nameof(Wrapper.Ok)).ToInt32();

    internal static void Check(IntPtr device, string operation)
    {
        // Do not marshal/dereference Message: 6.0.0 stores a dangling err.what() pointer.
        if (device == IntPtr.Zero || Marshal.ReadByte(device, OkOffset) == 0)
            throw Failure(operation);
    }

    internal static InvalidOperationException Failure(string operation) =>
        new($"RtMidi failed to {operation}. Native diagnostics are reported to stderr; RtMidi 6.0.0's error-message pointer cannot be safely read.");

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rtmidi_out_create(int api, [MarshalAs(UnmanagedType.LPUTF8Str)] string clientName);

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rtmidi_out_free(IntPtr device);

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rtmidi_get_port_count(IntPtr device);

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rtmidi_get_port_name(IntPtr device, uint portNumber, [Out] byte[]? buffer, ref int bufferLength);

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rtmidi_open_port(IntPtr device, uint portNumber, [MarshalAs(UnmanagedType.LPUTF8Str)] string portName);

    [DllImport("rtmidi", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rtmidi_out_send_message(IntPtr device, byte[] message, int length);
}
