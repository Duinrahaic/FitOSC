using System.Text.Json;
using FitOSC.Models;

namespace FitOSC.Services.Configuration;

/// <summary>
/// Service for managing application configuration
/// </summary>
public class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly string _configFilePath;
    private AppConfiguration _config;
    private bool _hasAutoConnected = false;

    /// <summary>
    /// Gets the path to the configuration file
    /// </summary>
    public static string ConfigFilePath
    {
        get
        {
            var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var fitOscFolder = Path.Combine(appDataFolder, "FitOSC");
            return Path.Combine(fitOscFolder, "config.json");
        }
    }

    /// <summary>
    /// Reads configuration directly from file without requiring DI.
    /// Useful for settings needed before services are initialized.
    /// </summary>
    public static AppConfiguration ReadConfigurationStatic()
    {
        try
        {
            var configPath = ConfigFilePath;
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfiguration>(json);
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch
        {
            // Silently fall back to defaults
        }

        return new AppConfiguration();
    }

    public ConfigurationService(ILogger<ConfigurationService> logger)
    {
        _logger = logger;

        // Store config in user's AppData folder
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fitOscFolder = Path.Combine(appDataFolder, "FitOSC");
        Directory.CreateDirectory(fitOscFolder);

        _configFilePath = Path.Combine(fitOscFolder, "config.json");
        _config = LoadConfiguration();
    }

    /// <summary>
    /// Get the current configuration
    /// </summary>
    public AppConfiguration GetConfiguration()
    {
        return _config;
    }

    /// <summary>
    /// Check if auto-connect has already been performed this session
    /// </summary>
    public bool HasAutoConnected()
    {
        return _hasAutoConnected;
    }

    /// <summary>
    /// Mark that auto-connect has been performed
    /// </summary>
    public void MarkAutoConnected()
    {
        _hasAutoConnected = true;
    }

    /// <summary>
    /// Update and save configuration
    /// </summary>
    public void SaveConfiguration(AppConfiguration config)
    {
        _config = config;

        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_configFilePath, json);
            _logger.LogInformation("Configuration saved to {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration");
        }
    }

    /// <summary>
    /// Load configuration from file
    /// </summary>
    private AppConfiguration LoadConfiguration()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<AppConfiguration>(json);

                if (config != null)
                {
                    _logger.LogInformation("Configuration loaded from {Path}", _configFilePath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration, using defaults");
        }

        _logger.LogInformation("Using default configuration");
        return new AppConfiguration();
    }
}
