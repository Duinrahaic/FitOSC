using System.Globalization;
using FitOSC.Models;
using FitOSC.Services.Configuration;

namespace FitOSC.Services;

/// <summary>
/// Formats dates and times with the user's chosen date order and clock.
/// </summary>
public class DateTimeFormatService
{
    private readonly ConfigurationService _configService;

    public DateTimeFormatService(ConfigurationService configService)
    {
        _configService = configService;
    }

    public DateFormat DateFormat => _configService.GetConfiguration().User.DateFormat ?? SystemDateFormat;

    public TimeFormat TimeFormat => _configService.GetConfiguration().User.TimeFormat ?? SystemTimeFormat;

    public static DateFormat SystemDateFormat
    {
        get
        {
            var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            return pattern.IndexOf('d') < pattern.IndexOf('M') ? DateFormat.DayMonth : DateFormat.MonthDay;
        }
    }

    public static TimeFormat SystemTimeFormat =>
        CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? TimeFormat.TwentyFourHour : TimeFormat.TwelveHour;

    /// <summary>Short day and month, e.g. 10/26 or 26/10.</summary>
    public string Date(DateTime value) => Date(value, DateFormat);

    /// <summary>Hours and minutes, e.g. 6:41 PM or 18:41.</summary>
    public string Time(DateTime value) => Time(value, TimeFormat);

    private static string Date(DateTime value, DateFormat format) =>
        value.ToString(format == DateFormat.DayMonth ? "d'/'M" : "M'/'d", CultureInfo.InvariantCulture);

    private static string Time(DateTime value, TimeFormat format) =>
        value.ToString(format == TimeFormat.TwentyFourHour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
}
