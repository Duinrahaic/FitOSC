namespace FitOSC.Services.Treadmill
{
    /// <summary>
    /// Represents FTMS treadmill measurement fields.
    /// Each property is a value + unit, and the snapshot carries the measurement type.
    /// </summary>
    public class TreadmillTelemetry
    {
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public Dictionary<TreadmillTelemetryProperty, TreadmillTelemetryValue> Values { get; init; } = CreateDefaultValues();
        public bool HasProperty(TreadmillTelemetryProperty telemetryProperty) => Values.ContainsKey(telemetryProperty);
        public TreadmillTelemetryValue? GetTelemetryValue(TreadmillTelemetryProperty telemetryProperty) => Values.GetValueOrDefault(telemetryProperty);

        /// <summary>
        /// Deep copy, so telemetry handed to other threads can't change underneath them.
        /// </summary>
        public TreadmillTelemetry Copy() => new()
        {
            Timestamp = Timestamp,
            Values = Values.ToDictionary(pair => pair.Key, pair => pair.Value.Copy())
        };

        public TreadmillTelemetrySnapshot Snapshot() => new()
        {
            Timestamp = Timestamp,
            Values = new System.Collections.ObjectModel.ReadOnlyDictionary<TreadmillTelemetryProperty, TreadmillTelemetryValueSnapshot>(
                Values.ToDictionary(pair => pair.Key, pair => new TreadmillTelemetryValueSnapshot(
                    pair.Value.TelemetryProperty, pair.Value.Value, pair.Value.MetricUnit, pair.Value.ImperialUnit, pair.Value.IsMetric)
                { Enabled = pair.Value.Enabled }))
        };

        private static Dictionary<TreadmillTelemetryProperty, TreadmillTelemetryValue> CreateDefaultValues() => new()
        {
            { TreadmillTelemetryProperty.InstantaneousSpeed, new TreadmillTelemetryValue(TreadmillTelemetryProperty.InstantaneousSpeed, 0, "KPH", "MPH") },
            { TreadmillTelemetryProperty.AverageSpeed,       new TreadmillTelemetryValue(TreadmillTelemetryProperty.AverageSpeed, 0, "KPH", "MPH") },
            { TreadmillTelemetryProperty.TotalDistance,      new TreadmillTelemetryValue(TreadmillTelemetryProperty.TotalDistance, 0, "KILOMETERS", "MILES") },
            { TreadmillTelemetryProperty.Incline,            new TreadmillTelemetryValue(TreadmillTelemetryProperty.Incline, 0, "%") },
            { TreadmillTelemetryProperty.RampAngle,          new TreadmillTelemetryValue(TreadmillTelemetryProperty.RampAngle, 0, "%") },
            { TreadmillTelemetryProperty.ElevationGain,      new TreadmillTelemetryValue(TreadmillTelemetryProperty.ElevationGain, 0, "METERS", "FEET") },
            { TreadmillTelemetryProperty.InstantaneousPace,  new TreadmillTelemetryValue(TreadmillTelemetryProperty.InstantaneousPace, 0, "PACE") },
            { TreadmillTelemetryProperty.AveragePace,        new TreadmillTelemetryValue(TreadmillTelemetryProperty.AveragePace, 0, "PACE") },
            { TreadmillTelemetryProperty.Calories,           new TreadmillTelemetryValue(TreadmillTelemetryProperty.Calories, 0, "KCAL") },
            { TreadmillTelemetryProperty.HeartRate,          new TreadmillTelemetryValue(TreadmillTelemetryProperty.HeartRate, 0, "BPM") },
            { TreadmillTelemetryProperty.MetabolicEquivalent,new TreadmillTelemetryValue(TreadmillTelemetryProperty.MetabolicEquivalent, 0, "MET") },
            { TreadmillTelemetryProperty.ElapsedTime,        new TreadmillTelemetryValue(TreadmillTelemetryProperty.ElapsedTime, 0, "SECS") },
            { TreadmillTelemetryProperty.RemainingTime,      new TreadmillTelemetryValue(TreadmillTelemetryProperty.RemainingTime, 0, "SECS") },
            { TreadmillTelemetryProperty.ForceOnBelt,        new TreadmillTelemetryValue(TreadmillTelemetryProperty.ForceOnBelt, 0, "NEWTONS") },
            { TreadmillTelemetryProperty.Power,              new TreadmillTelemetryValue(TreadmillTelemetryProperty.Power, 0, "WATTS") },
            { TreadmillTelemetryProperty.StepCount,          new TreadmillTelemetryValue(TreadmillTelemetryProperty.StepCount, 0, "STEPS") }
        };

    }
}
