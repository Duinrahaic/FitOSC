using System.Text.Json;
using Blazored.LocalStorage;

namespace FitOSC.Shared.Config;

public static class LocalStorageExtensions
{
    public static async Task<FitOscConfig> GetConfig(this ILocalStorageService localStorage)
    {
        try
        {
            var hasConfig = await localStorage.ContainKeyAsync("TMC_Config");

            if(!hasConfig)
            {
                await localStorage.SetItemAsync("TMC_Config", new FitOscConfig());
            }
            
            return await localStorage.GetItemAsync<FitOscConfig>("TMC_Config") ?? new();
        }
        catch
        {
            return new FitOscConfig();
        }
    }
    
    public static async Task SetConfig(this ILocalStorageService localStorage, FitOscConfig config)
    {
        await localStorage.SetItemAsync("TMC_Config", config);
    }
}