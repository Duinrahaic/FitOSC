using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Blazored.LocalStorage;
using FitOSC.Shared.Config;

namespace FitOSC.Shared.Services;


public class ConfigService
{
    private readonly ILocalStorageService _localStorage;
    private readonly ILogger<ConfigService> _logger;
    private FitOscConfig _config = new FitOscConfig();
    
    
    public ConfigService(IServiceProvider services)
    {
        _localStorage = services.GetService<ILocalStorageService>();
        _logger = services.GetService<ILogger<ConfigService>>();
        try
        {
            
        }
        catch(Exception ex)
        {
            _logger.LogError($"Failed to load config: {ex.Message}");
        }
    }
    
    public async Task SaveConfig(FitOscConfig config)
    {
        await _localStorage.SetItemAsync("TMC_Config", config);
    }
    
    public async Task<FitOscConfig> GetConfig()
    {
        var hasConfig = await _localStorage.ContainKeyAsync("TMC_Config");
        if(!hasConfig)
        {
            await _localStorage.SetItemAsync("TMC_Config", new FitOscConfig());
        }
        try
        {
            return await _localStorage.GetItemAsync<FitOscConfig>("TMC_Config");
        }
        catch
        {
            return new FitOscConfig();
        }
    }
}