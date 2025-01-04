using System.ComponentModel.DataAnnotations;

namespace FitOSC.Shared.Config;

public class FitOscConfig
{
    public bool IsMetric { get; set; } = false;
    public decimal UserMaxSpeed { get; set; } = 10m;

    public decimal EquipmentMaxSpeed { get; set; } = 10m;
    public decimal EquipmentMinSpeed { get; set; } = 0m;

    [Required]
    [Range(0.01, 1, ErrorMessage = "Increment must be between 0 and 1")]
    public decimal IncrementAmount { get; set; } = 0.16m;

    [Range(0.01, 10, ErrorMessage = "Default speed (kmh) must be between 0 and 10")]
    public decimal DefaultSpeed { get; set; } = 1.6m;
}