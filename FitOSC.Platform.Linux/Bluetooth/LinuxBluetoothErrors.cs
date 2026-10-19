using Tmds.DBus;

namespace FitOSC.Platform.Linux.Bluetooth;

internal static class LinuxBluetoothErrors
{
    // BlueZ 5.83 gdbus/object.c:1955-1966; libdbus 1.14.10 dbus-connection.c:4800.
    internal static bool IsObjectRemoved(DBusException ex) =>
        ex.ErrorName == "org.freedesktop.DBus.Error.UnknownObject";

    // BlueZ 5.83 gdbus/object.c:913-916 (Properties.GetAll).
    internal static bool IsDeviceInterfaceRemoved(DBusException ex) =>
        ex.ErrorName == "org.freedesktop.DBus.Error.InvalidArgs"
        && ex.ErrorMessage == "No such interface 'org.bluez.Device1'";

    // BlueZ 5.72 adapter.c:2878; 5.83 adapter.c:2905.
    internal static bool IsNoDiscoveryStarted(DBusException ex) =>
        ex.ErrorName == "org.bluez.Error.Failed" && ex.ErrorMessage == "No discovery started";

    // BlueZ 5.83 gatt-client.c:1705.
    internal static bool IsNoNotifySession(DBusException ex) =>
        ex.ErrorName == "org.bluez.Error.Failed" && ex.ErrorMessage == "No notify session started";

    // BlueZ src/error.h ERROR_INTERFACE; bus authorization/transport errors are excluded.
    internal static bool IsOperationFailure(DBusException ex) =>
        ex.ErrorName.StartsWith("org.bluez.Error.", StringComparison.Ordinal) || IsObjectRemoved(ex);
}
