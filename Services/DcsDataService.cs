using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LASTE_Mate.Models;

namespace LASTE_Mate.Services;

public class DcsDataService : IDisposable
{
    private FileSystemWatcher? _fileWatcher;
    private string? _exportFilePath;
    private bool _isWatching;
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private const int UpdateTimeoutSeconds = 5; // Consider disconnected if no update in 5 seconds

    public event EventHandler<DcsExportData?>? DataUpdated;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConnected => _isWatching && File.Exists(_exportFilePath) && 
                               (DateTime.Now - _lastUpdateTime).TotalSeconds < UpdateTimeoutSeconds;

    public string? ExportFilePath
    {
        get => _exportFilePath;
        set
        {
            if (_exportFilePath == value) return;
            
            StopWatching();
            _exportFilePath = value;
            
            if (!string.IsNullOrEmpty(_exportFilePath))
            {
                StartWatching();
            }
        }
    }

    public void StartWatching()
    {
        if (string.IsNullOrEmpty(_exportFilePath)) return;
        if (_isWatching) return;

        var directory = Path.GetDirectoryName(_exportFilePath);
        var fileName = Path.GetFileName(_exportFilePath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            return;

        try
        {
            // Ensure directory exists
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.Created += OnFileChanged;
            _isWatching = true;

            // Try to read initial file if it exists
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Wait a bit for file to be ready
                await ReadAndNotifyAsync();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error starting file watcher: {ex.Message}");
        }
    }

    public void StopWatching()
    {
        if (_fileWatcher != null)
        {
            _fileWatcher.Changed -= OnFileChanged;
            _fileWatcher.Created -= OnFileChanged;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
        _isWatching = false;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce rapid file changes
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await ReadAndNotifyAsync();
        });
    }

    public async Task ReadAndNotifyAsync()
    {
        if (string.IsNullOrEmpty(_exportFilePath) || !File.Exists(_exportFilePath))
        {
            NotifyConnectionStatus(false);
            return;
        }

        try
        {
            // Retry logic for file locking
            DcsExportData? data = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_exportFilePath);
                    data = JsonSerializer.Deserialize<DcsExportData>(json);
                    _lastUpdateTime = DateTime.Now;
                    NotifyConnectionStatus(true);
                    
                    // Debug logging
                    if (data != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"DcsDataService: Parsed data - Mission: {(data.Mission != null ? "exists" : "null")}, Source: {data.Source}, Timestamp: {data.Timestamp}");
                        if (data.Mission != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Mission Theatre: {data.Mission.Theatre}, Sortie: {data.Mission.Sortie}, StartTime: {data.Mission.StartTime}");
                        }
                    }
                    
                    DataUpdated?.Invoke(this, data);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(200);
                }
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading export file: {ex.Message}");
        }

        NotifyConnectionStatus(false);
    }

    private bool _lastConnectionStatus;

    private void NotifyConnectionStatus(bool connected)
    {
        if (_lastConnectionStatus != connected)
        {
            _lastConnectionStatus = connected;
            ConnectionStatusChanged?.Invoke(this, connected);
        }
    }

    public static string GetDefaultExportPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var savedGames = Path.Combine(userProfile, "Saved Games");
        
        // Try DCS.openbeta first (more common), then DCS
        var openBetaPath = Path.Combine(savedGames, "DCS.openbeta", "Scripts", "Export", "wind_data.json");
        var stablePath = Path.Combine(savedGames, "DCS", "Scripts", "Export", "wind_data.json");

        if (Directory.Exists(Path.GetDirectoryName(openBetaPath)))
        {
            return openBetaPath;
        }

        return Directory.Exists(Path.GetDirectoryName(stablePath)) ? stablePath : openBetaPath;
    }

    public void Dispose()
    {
        StopWatching();
    }
}

