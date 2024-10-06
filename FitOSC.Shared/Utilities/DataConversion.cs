namespace FitOSC.Shared.Utilities;
public static class DataConversion
{
    public static decimal ConvertKphToMph(decimal kph)
    {
        decimal value = kph * 0.621371m;
        return Math.Round(value,2); // 1 kilometer per hour is approximately 0.621371 miles per hour
    }

    public static decimal ConvertKilometersToMiles(decimal kilometers)
    {
        decimal value = kilometers * 0.621371m;
        return Math.Round(value,1); // 1 kilometer is approximately 0.621371 miles
    }
    public static decimal ConvertSpeedToMetersPerSecond(decimal speedKmh)
    {
        return speedKmh / 3.6m;
    }

    public static string ConvertSecondsToTime(int totalSeconds)
    {
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    public static string GetPacePerKilometer(decimal kph)
    {
        if (kph == 0) return "N/A"; // Avoid division by zero
        
        decimal minutesPerKilometer = 60 / kph;
        int minutes = (int)minutesPerKilometer;
        int seconds = (int)((minutesPerKilometer - minutes) * 60);
        
        return $"{minutes:D2}:{seconds:D2}";
    }

    public static string GetPacePerMile(decimal mph)
    {
        if (mph == 0) return "N/A"; // Avoid division by zero
        
        decimal minutesPerMile = 60 / mph;
        int minutes = (int)minutesPerMile;
        int seconds = (int)((minutesPerMile - minutes) * 60);
        
        return $"{minutes:D2}:{seconds:D2}";
    }

}