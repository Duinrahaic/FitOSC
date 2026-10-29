namespace FitOSC.Models;

/// <summary>
/// Represents a treadmill workout session
/// </summary>
public class TreadmillSession
{
    /// <summary>
    /// Unique identifier for the session
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// When the session started
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When the session ended
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Duration of the session in seconds
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>
    /// Total distance traveled in meters
    /// </summary>
    public decimal TotalDistance { get; set; }

    /// <summary>
    /// Average speed in km/h
    /// </summary>
    public decimal AverageSpeed { get; set; }

    /// <summary>
    /// Maximum speed reached in km/h
    /// </summary>
    public decimal MaxSpeed { get; set; }

    /// <summary>
    /// Total calories burned
    /// </summary>
    public int Calories { get; set; }

    /// <summary>
    /// Average heart rate (if available)
    /// </summary>
    public int? AverageHeartRate { get; set; }

    /// <summary>
    /// Maximum heart rate (if available)
    /// </summary>
    public int? MaxHeartRate { get; set; }

    /// <summary>
    /// Total elevation gain in meters (if available)
    /// </summary>
    public decimal? ElevationGain { get; set; }

    /// <summary>
    /// Total steps taken (if available)
    /// </summary>
    public int? Steps { get; set; }

    /// <summary>
    /// Average incline in percent (if available)
    /// </summary>
    public decimal? AverageIncline { get; set; }

    /// <summary>
    /// Maximum incline in percent (if available)
    /// </summary>
    public decimal? MaxIncline { get; set; }

    /// <summary>
    /// Average metabolic equivalent (if available)
    /// </summary>
    public decimal? AverageMet { get; set; }

    /// <summary>
    /// Maximum metabolic equivalent (if available)
    /// </summary>
    public decimal? MaxMet { get; set; }

    /// <summary>
    /// Average force on the belt in newtons (if available)
    /// </summary>
    public decimal? AverageForceOnBelt { get; set; }

    /// <summary>
    /// Maximum force on the belt in newtons (if available)
    /// </summary>
    public decimal? MaxForceOnBelt { get; set; }

    /// <summary>
    /// Average power output in watts (if available)
    /// </summary>
    public decimal? AveragePower { get; set; }

    /// <summary>
    /// Maximum power output in watts (if available)
    /// </summary>
    public decimal? MaxPower { get; set; }

    /// <summary>
    /// Device name used for this session
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Treadmill type used for this session
    /// </summary>
    public string? TreadmillType { get; set; }
}
