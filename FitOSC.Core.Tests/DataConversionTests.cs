using FitOSC.Utilities;
using Xunit;

namespace FitOSC.Core.Tests;

public class DataConversionTests
{
    [Theory]
    [InlineData(1, 0.62)]
    [InlineData(10, 6.21)]
    public void KilometersPerHourConvertsToRoundedMilesPerHour(decimal kph, decimal expectedMph)
    {
        Assert.Equal(expectedMph, kph.ConvertKphToMph());
    }

    [Theory]
    [InlineData(1, 1.61)]
    [InlineData(5, 8.05)]
    public void MilesPerHourConvertsToRoundedKilometersPerHour(decimal mph, decimal expectedKph)
    {
        Assert.Equal(expectedKph, mph.ConvertMphToKph());
    }
}
