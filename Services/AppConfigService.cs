using System;
using System.IO;
using System.Text.Json;
using LASTE_Mate.Models;
using LASTE_Mate.Serialization;
using NLog;

namespace LASTE_Mate.Services;

public class AppConfigService
{
    private static readonly ILogger Logger = LoggingService.GetLogger<AppConfigService>();
    private readonly string _configFilePath;
    private AppConfig _config;

    public AppConfigService()
    {
        // Get the directory where the executable is located
        // For single-file apps, use AppContext.BaseDirectory
        var configDirectory = AppContext.BaseDirectory;
        
        // Ensure the directory exists
        if (string.IsNullOrEmpty(configDirectory))
        {
            configDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }
        
        // Normalize the path
        configDirectory = Path.GetFullPath(configDirectory);
        
        // Ensure the directory exists (should already exist, but just in case)
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }
        
        _configFilePath = Path.Combine(configDirectory, "config.json");
        _config = LoadConfig();
    }

    public AppConfig GetConfig()
    {
        return _config;
    }

    private static readonly AppConfigJsonContext JsonContext = AppConfigJsonContext.Default;

    public void SaveConfig(AppConfig config)
    {
        try
        {
            _config = config;
            var json = JsonSerializer.Serialize(config, JsonContext.AppConfig);
            File.WriteAllText(_configFilePath, json);
            Logger.Debug("Config saved to {ConfigFilePath}", _configFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving config");
        }
    }

    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize(json, JsonContext.AppConfig);
                if (config != null)
                {
                    // Migration: Enable TCP listener autostart by default
                    // - If property is missing (old config): enable it
                    // - If property is false (user had it disabled): migrate to true for autostart
                    var needsMigration = !json.Contains("tcpListenerEnabled", StringComparison.OrdinalIgnoreCase) || 
                                        !config.TcpListenerEnabled;
                    
                    if (needsMigration)
                    {
                        config.TcpListenerEnabled = true;
                        Logger.Info("Config migration: Enabling TCP listener autostart");
                        // Save the migrated config
                        SaveConfig(config);
                    }
                    
                    Logger.Debug("Config loaded from {ConfigFilePath}, TcpListenerEnabled={TcpListenerEnabled}", _configFilePath, config.TcpListenerEnabled);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error loading config");
        }

        // Return default config (for new installations)
        var defaultConfig = new AppConfig
        {
            TcpPort = 10309,
            AutoUpdate = true,
            DcsBiosPort = 7778,
            TcpListenerEnabled = true
        };
        
        // Save the default config to file so it exists next time
        try
        {
            SaveConfig(defaultConfig);
            Logger.Info("Created default config file at {ConfigFilePath}", _configFilePath);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create default config file, using in-memory defaults");
        }
        
        return defaultConfig;
    }
}

