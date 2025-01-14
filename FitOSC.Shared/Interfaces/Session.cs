namespace FitOSC.Shared.Interfaces;

public class Session
{
    public DateTime DateTime { get; set; } = DateTime.Now.ToUniversalTime();
    public decimal Calories { get; set; } = 0;
    public decimal Distance { get; set; } = 0;
    public decimal ElapsedTime { get; set; } = 0;
}