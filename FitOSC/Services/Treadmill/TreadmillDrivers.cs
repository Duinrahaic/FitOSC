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
    private static readonly Dictionary<TreadmillType, TreadmillDriver> ByType = Discover();

    public static IReadOnlyList<TreadmillDriver> All { get; } = ByType.Values.OrderBy(driver => driver.Type).ToList();

    public static TreadmillDriver? Find(TreadmillType type) => ByType.GetValueOrDefault(type);

    /// <summary>Finds every <see cref="TreadmillDriverAttribute"/> service, failing on the first one registered wrongly.</summary>
    private static Dictionary<TreadmillType, TreadmillDriver> Discover()
    {
        var drivers = new Dictionary<TreadmillType, TreadmillDriver>();
        foreach (var type in typeof(TreadmillDrivers).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<TreadmillDriverAttribute>() is not { } attribute)
                continue;

            if (!typeof(ITreadmillService).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidOperationException($"{type.Name} is marked as a treadmill driver but is not a concrete treadmill service");
            if (type.GetConstructors().Length != 1)
                throw new InvalidOperationException($"Treadmill driver {type.Name} must have exactly one public constructor");
            if (drivers.TryGetValue(attribute.Type, out var existing))
                throw new InvalidOperationException(
                    $"Treadmill drivers {existing.ServiceType.Name} and {type.Name} are both registered as {attribute.Type}");

            drivers[attribute.Type] = new TreadmillDriver(
                attribute.Type,
                attribute.Name,
                attribute.Description,
                attribute.Experimental,
                attribute.AdvertisedServices.Select(Guid.Parse).ToList(),
                type);
        }

        return drivers;
    }

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
