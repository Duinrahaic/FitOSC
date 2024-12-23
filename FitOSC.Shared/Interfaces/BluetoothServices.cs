using System.Reflection;
using Blazor.Bluetooth;
using FitOSC.Shared.Extensions;

namespace FitOSC.Shared.Interfaces;

public static class BluetoothServices
{
    public const string FTMS = "00001826-0000-1000-8000-00805f9b34fb";
    public const string WalkingMachine = "0x0000FE00-0000-1000-8000-00805F9B34FB";
    
    public static List<string> GetServices()
    {
        Type objType = typeof(BluetoothServices);
        var fieldValues = objType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();
        return fieldValues ?? new();
    }
}