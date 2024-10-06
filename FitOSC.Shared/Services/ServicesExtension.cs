using Blazor.Bluetooth;
using Blazored.LocalStorage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FitOSC.Shared.Services;

public static class ServicesExtension
{
    public static IServiceCollection RegisterServices(this IServiceCollection services)
    {
        // if webassmebly 
        if (!OperatingSystem.IsBrowser())
        {
            services.AddScoped<OscService>();
        }
        services.AddBluetoothNavigator();
        services.AddBlazoredLocalStorage();
        services.AddScoped<ConfigService>();
        return services;
    }
    
    
    
    public static void RegisterHostedService<TService>(this IServiceCollection services)
        where TService : class, IHostedService
    {
        // Add the service as a singleton
        services.AddSingleton<TService>();

        // Add the service as a hosted service
        services.AddHostedService<TService>(provider => provider.GetRequiredService<TService>());
    } 
}