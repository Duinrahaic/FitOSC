using System.Runtime.InteropServices;

namespace FitOSC.Platform.Linux;

public static class FatalErrorDialog
{
    private const string Gtk = "libgtk-3.so.0";

    // Called by the shell's catch on the main STA thread, after stderr and exit code 1.
    public static void Show(string message)
    {
        try
        {
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
                return;

            var dialog = gtk_dialog_new();
            try
            {
                gtk_window_set_title(dialog, "FitOSC fatal error");
                gtk_dialog_add_button(dialog, "_Close", -7); // GTK_RESPONSE_CLOSE
                var scroll = gtk_scrolled_window_new(IntPtr.Zero, IntPtr.Zero);
                var label = gtk_label_new(message);
                gtk_label_set_selectable(label, 1);
                gtk_label_set_line_wrap(label, 1);
                gtk_container_add(scroll, label);
                gtk_widget_set_size_request(scroll, 600, 400);
                gtk_box_pack_start(gtk_dialog_get_content_area(dialog), scroll, 1, 1, 12);
                gtk_widget_show_all(dialog);
                gtk_dialog_run(dialog);
            }
            finally
            {
                gtk_widget_destroy(dialog);
            }
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("FitOSC fatal dialog unavailable: GTK3 is not installed.");
        }
    }

    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_dialog_new();
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_window_set_title(IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_dialog_add_button(IntPtr dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int responseId);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_dialog_get_content_area(IntPtr dialog);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_scrolled_window_new(IntPtr horizontalAdjustment, IntPtr verticalAdjustment);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr gtk_label_new([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_label_set_selectable(IntPtr label, int selectable);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_label_set_line_wrap(IntPtr label, int wrap);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_box_pack_start(IntPtr box, IntPtr child, int expand, int fill, uint padding);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_dialog_run(IntPtr dialog);
    [DllImport(Gtk, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_destroy(IntPtr widget);
}
