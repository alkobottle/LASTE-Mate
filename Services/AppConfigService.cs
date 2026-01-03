using System;
using System.IO;
using System.Text.Json;
using LASTE_Mate.Models;

namespace LASTE_Mate.Services;

public class AppConfigService
{
    private readonly string _configFilePath;
    private AppConfig _config;

    public AppConfigService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appDataPath, "LASTE-Mate");
        
        if (!Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
        }
        
        _configFilePath = Path.Combine(configDir, "config.json");
        _config = LoadConfig();
    }

    public AppConfig GetConfig()
    {
        return _config;
    }

    public void SaveConfig(AppConfig config)
    {
        try
        {
            _config = config;
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_configFilePath, json);
            Console.WriteLine($"Config saved to {_configFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
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
                        Console.WriteLine("Config migration: Enabling TCP listener autostart");
                        // Save the migrated config
                        SaveConfig(config);
                    }
                    
                    Console.WriteLine($"Config loaded from {_configFilePath}, TcpListenerEnabled={config.TcpListenerEnabled}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading config: {ex.Message}");
        }

        // Return default config (for new installations)
               return new AppConfig
               {
                   ConnectionMode = "TcpSocket",
                   TcpPort = 10309,
                   ExportFilePath = DcsDataService.GetDefaultExportPath(),
                   AutoUpdate = true,
            DcsBiosPort = 7778,
            TcpListenerEnabled = true
               };
    }
}

