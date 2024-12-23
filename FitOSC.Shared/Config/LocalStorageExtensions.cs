using System.Text.Json;
using Blazored.LocalStorage;
using FitOSC.Shared.Interfaces;

namespace FitOSC.Shared.Config;

public static class LocalStorageExtensions
{
    private static async Task<T?> GetAsync<T>(this ILocalStorageService localStorage, string key)
    {
        try
        {
            var item = await localStorage.GetItemAsStringAsync(key);
            return item == null ? default : JsonSerializer.Deserialize<T>(item);
        }catch
        {
            return default;
        }
    }
    private static async Task SetAsync<T>(this ILocalStorageService localStorage, string key, T item)
    {
        try
        {
            await localStorage.SetItemAsStringAsync(key, JsonSerializer.Serialize(item));
        }
        catch
        {
            // ignored
        }
    }
    public static async Task<FitOscConfig> GetConfig(this ILocalStorageService localStorage) =>
        await localStorage.GetAsync<FitOscConfig>("TMC_Config") ?? new();
    
    public static async Task SetConfig(this ILocalStorageService localStorage, FitOscConfig config) => 
        await localStorage.SetAsync("TMC_Config", config);
    
    public static async Task<Session?> GetLastSession(this ILocalStorageService localStorage) =>
        await localStorage.GetAsync<Session>("TMC_LastSession");
    public static async Task SetLastSession(this ILocalStorageService localStorage, Session session) => 
        await localStorage.SetAsync("TMC_LastSession", session);

    public static async Task<Session> GetLastMonthlySession(this ILocalStorageService localStorage) =>
        await localStorage.GetAsync<Session>("TMC_LastMonthlySession") ?? new()
        {
            DateTime = DateTime.Now.ToUniversalTime().AddMonths(-1)
        };
    public static async Task SetLastMonthlySession(this ILocalStorageService localStorage, Session session) => 
        await localStorage.SetAsync("TMC_LastMonthlySession", session);
    
    public static async Task<Session> GetMonthlySession(this ILocalStorageService localStorage) =>
        await localStorage.GetAsync<Session>("TMC_MonthlySession") ?? new();
    public static async Task SetMonthlySession(this ILocalStorageService localStorage, Session session) => 
        await localStorage.SetAsync("TMC_MonthlySession", session);
}