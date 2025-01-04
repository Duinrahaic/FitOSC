using System.ComponentModel;
using System.Reflection;

namespace FitOSC.Shared.Interfaces;

public class FitnessMachineFeatures
{
    [DisplayName("Average Speed ")] public bool AverageSpeedSupported { get; set; }

    [DisplayName("Cadence")] public bool CadenceSupported { get; set; }

    [DisplayName("Total Distance")] public bool TotalDistanceSupported { get; set; }

    [DisplayName("Inclination")] public bool InclinationSupported { get; set; }

    [DisplayName("Elevation Gain")] public bool ElevationGainSupported { get; set; }

    [DisplayName("Pace")] public bool PaceSupported { get; set; }

    [DisplayName("Step Count")] public bool StepCountSupported { get; set; }

    [DisplayName("Resistance Level")] public bool ResistanceLevelSupported { get; set; }

    [DisplayName("Stride Count")] public bool StrideCountSupported { get; set; }

    [DisplayName("Expended Energy")] public bool ExpendedEnergySupported { get; set; }

    [DisplayName("Heart Rate Measurement")]
    public bool HeartRateMeasurementSupported { get; set; }

    [DisplayName("Metabolic Equivalent")] public bool MetabolicEquivalentSupported { get; set; }

    [DisplayName("Elapsed Time")] public bool ElapsedTimeSupported { get; set; }

    [DisplayName("Remaining Time")] public bool RemainingTimeSupported { get; set; }

    [DisplayName("Power Measurement")] public bool PowerMeasurementSupported { get; set; }

    [DisplayName("Force On Belt And Power Output")]
    public bool ForceOnBeltAndPowerOutputSupported { get; set; }

    [DisplayName("User Data Retention")] public bool UserDataRetentionSupported { get; set; }

    // Target Setting Features (4.3.1.2)
    [DisplayName("Speed Target")] public bool SpeedTargetSettingSupported { get; set; }

    [DisplayName("Inclination Target")] public bool InclinationTargetSettingSupported { get; set; }

    [DisplayName("Resistance Target")] public bool ResistanceTargetSettingSupported { get; set; }

    [DisplayName("Power Target")] public bool PowerTargetSettingSupported { get; set; }

    [DisplayName("Heart Rate Target")] public bool HeartRateTargetSettingSupported { get; set; }

    [DisplayName("Targeted Expended Energy Configuration")]
    public bool TargetedExpendedEnergyConfigurationSupported { get; set; }

    [DisplayName("Targeted Step Number Configuration")]
    public bool TargetedStepNumberConfigurationSupported { get; set; }

    [DisplayName("Targeted Stride Number Configuration")]
    public bool TargetedStrideNumberConfigurationSupported { get; set; }

    [DisplayName("Targeted Distance Configuration")]
    public bool TargetedDistanceConfigurationSupported { get; set; }

    [DisplayName("Targeted Training Time Configuration")]
    public bool TargetedTrainingTimeConfigurationSupported { get; set; }

    [DisplayName("Targeted Time In Two Heart Rate Zones")]
    public bool TargetedTimeInTwoHeartRateZonesSupported { get; set; }

    [DisplayName("Targeted Time In Three Heart Rate Zones")]
    public bool TargetedTimeInThreeHeartRateZonesSupported { get; set; }

    [DisplayName("Targeted Time In Five Heart Rate Zones")]
    public bool TargetedTimeInFiveHeartRateZonesSupported { get; set; }

    [DisplayName("Indoor Bike Simulation Parameters")]
    public bool IndoorBikeSimulationParametersSupported { get; set; }

    [DisplayName("Wheel Circumference Configuration")]
    public bool WheelCircumferenceConfigurationSupported { get; set; }

    [DisplayName("Spin Down Control")] public bool SpinDownControlSupported { get; set; }

    [DisplayName("Targeted Cadence Configuration")]
    public bool TargetedCadenceConfigurationSupported { get; set; }

    public static FitnessMachineFeatures Parse(byte[] data)
    {
        if (data == null || data.Length < 8)
            throw new ArgumentException("Invalid data length. Must be at least 8 bytes.");

        var features = new FitnessMachineFeatures();

        // Parse Fitness Machine Features (first 4 bytes)
        var fitnessFeatures = BitConverter.ToUInt32(data, 0);
        features.AverageSpeedSupported = (fitnessFeatures & (1 << 0)) != 0;
        features.CadenceSupported = (fitnessFeatures & (1 << 1)) != 0;
        features.TotalDistanceSupported = (fitnessFeatures & (1 << 2)) != 0;
        features.InclinationSupported = (fitnessFeatures & (1 << 3)) != 0;
        features.ElevationGainSupported = (fitnessFeatures & (1 << 4)) != 0;
        features.PaceSupported = (fitnessFeatures & (1 << 5)) != 0;
        features.StepCountSupported = (fitnessFeatures & (1 << 6)) != 0;
        features.ResistanceLevelSupported = (fitnessFeatures & (1 << 7)) != 0;
        features.StrideCountSupported = (fitnessFeatures & (1 << 8)) != 0;
        features.ExpendedEnergySupported = (fitnessFeatures & (1 << 9)) != 0;
        features.HeartRateMeasurementSupported = (fitnessFeatures & (1 << 10)) != 0;
        features.MetabolicEquivalentSupported = (fitnessFeatures & (1 << 11)) != 0;
        features.ElapsedTimeSupported = (fitnessFeatures & (1 << 12)) != 0;
        features.RemainingTimeSupported = (fitnessFeatures & (1 << 13)) != 0;
        features.PowerMeasurementSupported = (fitnessFeatures & (1 << 14)) != 0;
        features.ForceOnBeltAndPowerOutputSupported = (fitnessFeatures & (1 << 15)) != 0;
        features.UserDataRetentionSupported = (fitnessFeatures & (1 << 16)) != 0;

        // Parse Target Setting Features (next 4 bytes)
        var targetFeatures = BitConverter.ToUInt32(data, 4);
        features.SpeedTargetSettingSupported = (targetFeatures & (1 << 0)) != 0;
        features.InclinationTargetSettingSupported = (targetFeatures & (1 << 1)) != 0;
        features.ResistanceTargetSettingSupported = (targetFeatures & (1 << 2)) != 0;
        features.PowerTargetSettingSupported = (targetFeatures & (1 << 3)) != 0;
        features.HeartRateTargetSettingSupported = (targetFeatures & (1 << 4)) != 0;
        features.TargetedExpendedEnergyConfigurationSupported = (targetFeatures & (1 << 5)) != 0;
        features.TargetedStepNumberConfigurationSupported = (targetFeatures & (1 << 6)) != 0;
        features.TargetedStrideNumberConfigurationSupported = (targetFeatures & (1 << 7)) != 0;
        features.TargetedDistanceConfigurationSupported = (targetFeatures & (1 << 8)) != 0;
        features.TargetedTrainingTimeConfigurationSupported = (targetFeatures & (1 << 9)) != 0;
        features.TargetedTimeInTwoHeartRateZonesSupported = (targetFeatures & (1 << 10)) != 0;
        features.TargetedTimeInThreeHeartRateZonesSupported = (targetFeatures & (1 << 11)) != 0;
        features.TargetedTimeInFiveHeartRateZonesSupported = (targetFeatures & (1 << 12)) != 0;
        features.IndoorBikeSimulationParametersSupported = (targetFeatures & (1 << 13)) != 0;
        features.WheelCircumferenceConfigurationSupported = (targetFeatures & (1 << 14)) != 0;
        features.SpinDownControlSupported = (targetFeatures & (1 << 15)) != 0;
        features.TargetedCadenceConfigurationSupported = (targetFeatures & (1 << 16)) != 0;

        return features;
    }

    public Dictionary<string, bool> ToDictionary()
    {
        var result = new Dictionary<string, bool>();
        var properties = typeof(FitnessMachineFeatures).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var displayAttribute = property.GetCustomAttribute<DisplayNameAttribute>();
            if (displayAttribute != null)
                result[displayAttribute.DisplayName ?? property.Name] = property.GetValue(this) as bool? ?? false;
        }

        return result;
    }
}