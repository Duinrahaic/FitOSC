namespace FitOSC.Shared.Interfaces;

public class FtmsData
{
    public decimal Speed { get; set; }
    public decimal Distance { get; set; } = 0;
    public double? Inclination { get; set; }
    public int Kcal { get; set; } = 0;
    public int? HeartRate { get; set; }
    public int ElapsedTime { get; set; } = 0;
    public ushort? RemainingTime { get; set; }
    public double? ElevGain { get; set; }
    public int? MET { get; set; }
    public int? InsPace { get; set; } 
    public int? AvgPace { get; set; } 
}