using System.Reflection;
using FitOSC.Services.State;

namespace FitOSC.Services.Treadmill;

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public sealed record TreadmillDriver(
    TreadmillType Type,
    string Name,
    string Description,
    bool Experimental,
    IReadOnlyList<Guid> AdvertisedServices,
    Type ServiceType);

/// <summary>
/// Experimental: Evaluating driver
/// </summary>
public static class TreadmillDrivers
{
    private static readonly Dictionary<TreadmillType, TreadmillDriver> ByType = typeof(TreadmillDrivers).Assembly
        .GetTypes()
        .Where(type => typeof(ITreadmillService).IsAssignableFrom(type) && !type.IsAbstract)
        .Select(type => (Type: type, Attribute: type.GetCustomAttribute<TreadmillDriverAttribute>()))
        .Where(entry => entry.Attribute != null)
        .Select(entry => new TreadmillDriver(
            entry.Attribute!.Type,
            entry.Attribute.Name,
            entry.Attribute.Description,
            entry.Attribute.Experimental,
            entry.Attribute.AdvertisedServices.Select(Guid.Parse).ToList(),
            entry.Type))
        .ToDictionary(driver => driver.Type);

    public static IReadOnlyList<TreadmillDriver> All { get; } = ByType.Values.OrderBy(driver => driver.Type).ToList();

    public static TreadmillDriver? Find(TreadmillType type) => ByType.GetValueOrDefault(type);

    /// <summary>
    /// Creates the driver's service. Driver constructors take a logger (<see cref="ILogger"/> or
    /// <see cref="ILogger{TCategoryName}"/>) and the <see cref="AppStateService"/>.
    /// </summary>
    public static ITreadmillService Create(TreadmillDriver driver, ILoggerFactory loggerFactory, AppStateService appState)
    {
        var constructor = driver.ServiceType.GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(AppStateService) => appState,
            var type when type == typeof(ILogger) => loggerFactory.CreateLogger(driver.ServiceType),
            var type when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>) =>
                Activator.CreateInstance(typeof(Logger<>).MakeGenericType(type.GenericTypeArguments), loggerFactory)!,
            var type => throw new InvalidOperationException(
                $"{driver.ServiceType.Name} has an unsupported constructor parameter of type {type.Name}")
        }).ToArray();

        return (ITreadmillService)constructor.Invoke(arguments);
    }
}
