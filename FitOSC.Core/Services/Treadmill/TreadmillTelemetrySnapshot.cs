using System.Collections.ObjectModel;

namespace FitOSC.Services.Treadmill;

public sealed record TreadmillTelemetrySnapshot
{
    public DateTime Timestamp { get; init; }
    public IReadOnlyDictionary<TreadmillTelemetryProperty, TreadmillTelemetryValueSnapshot> Values { get; init; } =
        new ReadOnlyDictionary<TreadmillTelemetryProperty, TreadmillTelemetryValueSnapshot>(new Dictionary<TreadmillTelemetryProperty, TreadmillTelemetryValueSnapshot>());
    public bool HasProperty(TreadmillTelemetryProperty property) => Values.ContainsKey(property);
    public TreadmillTelemetryValueSnapshot? GetTelemetryValue(TreadmillTelemetryProperty property) => Values.GetValueOrDefault(property);
}
