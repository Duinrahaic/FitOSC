namespace FitOSC.Utilities;
public static class DataConversion
{
    public static decimal ConvertKphToMph(this decimal kph)
    {
        decimal value = kph * 0.621371m;
        return Math.Round(value,2); // 1 kilometer per hour is approximately 0.621371 miles per hour
    }

    public static decimal ConvertMphToKph(this decimal mph)
    {
        decimal value = mph * 1.609344m;
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    } 
}