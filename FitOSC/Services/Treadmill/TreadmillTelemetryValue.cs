using FitOSC.Pages.Components;
using FitOSC.Utilities;

namespace FitOSC.Services.Treadmill;

public class TreadmillTelemetryValue
{
    
    public bool Enabled { get; set; } = false;
    
    /// <summary>
    /// The type of treadmill measurement (e.g. Speed, Distance).
    /// </summary>
    public TreadmillTelemetryProperty TelemetryProperty { get; init; }

    /// <summary>
    /// Raw telemetryProperty value (always stored in telemetryProperty).
    /// </summary>
    public decimal Value { get; set; }

    /// <summary>
    /// Unit string in TelemetryProperty by default.
    /// </summary>
    public string MetricUnit { get; set; } = string.Empty;

    /// <summary>
    /// Unit string in Imperial if applicable.
    /// </summary>
    public string ImperialUnit { get; set; } = string.Empty;

    public TreadmillTelemetryValue(TreadmillTelemetryProperty telemetryProperty, decimal value, string metricUnit, string imperialUnit = "")
    {
        TelemetryProperty = telemetryProperty;
        Value = value;
        MetricUnit = metricUnit;
        ImperialUnit = imperialUnit;
    }

    /// <summary>
    /// Return the value in TelemetryProperty units.
    /// </summary>
    public TreadmillTelemetryValue AsMetric()
    {
        return new TreadmillTelemetryValue(TelemetryProperty, Value, MetricUnit, ImperialUnit);
    }

    /// <summary>
    /// Return the value in Imperial units (converts if applicable).
    /// </summary>
    public TreadmillTelemetryValue AsImperial()
    {
        decimal converted = Value;

        switch (TelemetryProperty)
        {
            case TreadmillTelemetryProperty.InstantaneousSpeed:
            case TreadmillTelemetryProperty.AverageSpeed:
                converted = Value.ConvertKphToMph(); // km/h → mi/h
                break;
            case TreadmillTelemetryProperty.InstantaneousPace: // sec/km → sec/mile
                converted = Value.ConvertMphToKph();
                break;
            case TreadmillTelemetryProperty.TotalDistance:
                converted = Value * 0.000621371m; // kilometers → miles
                break;
            case TreadmillTelemetryProperty.ElevationGain:
                converted = Value * 3.28084m;  // meters → feet
                break;

            // Incline (%), RampAngle (%), Calories, HR, MET, Time, Force, Power, Steps don't change
            default:
                break;
        }

        return new TreadmillTelemetryValue(TelemetryProperty, Math.Round(converted, 2), MetricUnit, ImperialUnit);
    }

    public override string ToString()
    {
        return $"{Value:0.##} {MetricUnit}";
    }
}