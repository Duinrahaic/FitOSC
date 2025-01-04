using System.Reflection;
using FitOSC.Shared.Extensions;

namespace FitOSC.Shared.Interfaces;

public static class BluetoothServices
{
    public const string FTMS = "00001826-0000-1000-8000-00805f9b34fb";
    public const string WalkingMachine = "0000FE00-0000-1000-8000-00805F9B34FB";

    public static List<string> GetServices()
    {
        var objType = typeof(BluetoothServices);
        var fieldValues = objType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!).Select(x => x.ToLower())
            .ToList();
        return fieldValues ?? new List<string>();
    }

    public static List<string> GetServiceAlaiases()
    {
        return GetServices().Select(x => $"0x{BluetoothExtensions.SegmentUUID(x)}".ToLower()).ToList();
    }
}