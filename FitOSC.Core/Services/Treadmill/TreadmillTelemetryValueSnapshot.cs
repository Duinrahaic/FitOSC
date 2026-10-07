namespace FitOSC.Services.Treadmill;

public sealed record TreadmillTelemetryValueSnapshot(
    TreadmillTelemetryProperty TelemetryProperty,
    decimal Value,
    string MetricUnit,
    string ImperialUnit = "",
    bool IsMetric = true)
{
    public bool Enabled { get; init; }

    private TreadmillTelemetryValue ToValue() =>
        new(TelemetryProperty, Value, MetricUnit, ImperialUnit, IsMetric) { Enabled = Enabled };

    public TreadmillTelemetryValue AsMetric() => ToValue().AsMetric();
    public TreadmillTelemetryValue AsImperial() => ToValue().AsImperial();
    public override string ToString() => ToValue().ToString();
}
