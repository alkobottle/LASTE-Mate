using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LASTE_Mate.Core;
using LASTE_Mate.Models;
using LASTE_Mate.Services;

namespace LASTE_Mate.ViewModels;

public enum ConnectionMode
{
    FileBased,
    TcpSocket
}

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DcsDataService _dcsDataService;
    private readonly DcsSocketService _dcsSocketService;
    private readonly DcsBiosService _dcsBiosService;
    private readonly CduButtonSequence _cduButtonSequence;
    private readonly AppConfigService _configService;

    private bool _initializing = true;
    private bool _batchUpdating;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<string> _availableMaps;
    [ObservableProperty] private string _selectedMap = "Caucasus";

    [ObservableProperty] private int? _groundTempC = 15;

    [ObservableProperty] private double? _groundWindSpeed = 0;
    [ObservableProperty] private double? _groundWindDirection = 0;

    [ObservableProperty] private double? _wind2000mSpeed = 0;
    [ObservableProperty] private double? _wind2000mDirection = 0;

    [ObservableProperty] private double? _wind8000mSpeed = 0;
    [ObservableProperty] private double? _wind8000mDirection = 0;

    [ObservableProperty] private string _exportFilePath = string.Empty;

    [ObservableProperty] private bool _isDcsConnected;

    [ObservableProperty] private bool _autoUpdate = true;

    [ObservableProperty] private ObservableCollection<CduWindLineViewModel> _results = new();

    [ObservableProperty] private string? _missionTheatre;
    [ObservableProperty] private string? _missionSortie;
    [ObservableProperty] private string? _missionStartTime;

    [ObservableProperty] private string? _dataSource;
    [ObservableProperty] private string? _dataTimestamp;

    [ObservableProperty] private bool _isMapMatched = true;

    [ObservableProperty] private ConnectionMode _connectionMode = ConnectionMode.TcpSocket;

    [ObservableProperty] private bool _isTcpConnected;
    [ObservableProperty] private int _tcpPort = 10309;

    [ObservableProperty] private bool _isTcpServerRunning;

    [ObservableProperty] private string? _listenerError;

    [ObservableProperty] private bool _isSendingToCdu;
    partial void OnIsSendingToCduChanged(bool value) => RaiseCanSendToCdu();

    [ObservableProperty] private string _cduSendProgress = string.Empty;

    [ObservableProperty] private ObservableCollection<string> _cduDebugLog = new();

    [ObservableProperty] private bool _isDebugLogExpanded;
    [ObservableProperty] private bool _isSettingsExpanded = false;

    [ObservableProperty] private string _appVersion = VersionHelper.GetVersion();

    private CancellationTokenSource? _cduSendCancellationTokenSource;

    public MainWindowViewModel()
    {
        _dcsDataService = new DcsDataService();
        _dcsSocketService = new DcsSocketService();
        _dcsBiosService = new DcsBiosService();
        _cduButtonSequence = new CduButtonSequence(_dcsBiosService);
        _configService = new AppConfigService();

        _availableMaps = new ObservableCollection<string>(WindRecalculator.GetAvailableMaps());

        // Subscribe first (no side effects in handlers)
        _dcsDataService.DataUpdated += OnDcsDataUpdated;
        _dcsDataService.ConnectionStatusChanged += OnFileConnectionStatusChanged;

        _dcsSocketService.DataReceived += OnDcsDataUpdated;
        _dcsSocketService.ConnectionStatusChanged += OnTcpConnectionStatusChanged;
        _dcsSocketService.ListenerError += OnListenerError;

        // Load config without triggering side effects
        LoadConfig();

        // Apply initial mode exactly once
        ApplyConnectionMode();

        _initializing = false;
    }

    public bool CanSendToCdu => Results.Count > 0;

    private void LoadConfig()
    {
        var config = _configService.GetConfig();

        _batchUpdating = true;
        try
        {
            ExportFilePath = config.ExportFilePath ?? DcsDataService.GetDefaultExportPath();
            TcpPort = config.TcpPort <= 0 ? 10309 : config.TcpPort;
            AutoUpdate = config.AutoUpdate;

            // Set ConnectionMode (OnConnectionModeChanged is guarded by _initializing)
            if (Enum.TryParse<ConnectionMode>(config.ConnectionMode, out var mode))
                ConnectionMode = mode;
            else
                ConnectionMode = ConnectionMode.TcpSocket;
        }
        finally
        {
            _batchUpdating = false;
        }
    }

    private void SaveConfig()
    {
        var config = new AppConfig
        {
            ConnectionMode = ConnectionMode.ToString(),
            TcpPort = TcpPort,
            ExportFilePath = ExportFilePath,
            AutoUpdate = AutoUpdate
        };

        _configService.SaveConfig(config);
    }

    private void ApplyConnectionMode()
    {
        ListenerError = null;

        if (ConnectionMode == ConnectionMode.FileBased)
        {
            StopTcpServerInternal();

            _dcsDataService.ExportFilePath = ExportFilePath;
            _dcsDataService.StartWatching();

            UpdateOverallConnectionFlags();
            RaiseCanSendToCdu();
            return;
        }

        // TcpSocket
        _dcsDataService.StopWatching();
        StopTcpServerInternal(); // Always start stopped, user must click "Start TCP Server"

        UpdateOverallConnectionFlags();
        RaiseCanSendToCdu();
    }

    private void UpdateOverallConnectionFlags()
    {
        if (ConnectionMode == ConnectionMode.FileBased)
        {
            IsDcsConnected = _dcsDataService.IsConnected;
            IsTcpConnected = false;
        }
        else
        {
            IsTcpConnected = _dcsSocketService.IsConnected;
            IsDcsConnected = IsTcpConnected;
        }
    }

    private void RaiseCanSendToCdu()
    {
        OnPropertyChanged(nameof(CanSendToCdu));
    }

    private void OnFileConnectionStatusChanged(object? sender, bool connected)
    {
        if (ConnectionMode != ConnectionMode.FileBased) return;

        Dispatcher.UIThread.Post(() =>
        {
            IsDcsConnected = connected;
        });
    }

    private void OnTcpConnectionStatusChanged(object? sender, bool connected)
    {
        if (ConnectionMode != ConnectionMode.TcpSocket) return;

        Dispatcher.UIThread.Post(() =>
        {
            IsTcpConnected = connected;
            IsDcsConnected = connected;
            RaiseCanSendToCdu();
        });
    }

    private void OnListenerError(string errorMessage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ListenerError = errorMessage;
        });
    }

    private void OnDcsDataUpdated(object? sender, DcsExportData? data)
    {
        if (data == null) return;

        // Always marshal to UI thread
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnDcsDataUpdated(sender, data));
            return;
        }

        _batchUpdating = true;
        try
        {
            if (data.Ground != null)
            {
                GroundWindSpeed = data.Ground.Speed;
                GroundWindDirection = data.Ground.Direction;
            }

            if (data.At2000m != null)
            {
                Wind2000mSpeed = data.At2000m.Speed;
                Wind2000mDirection = data.At2000m.Direction;
            }

            if (data.At8000m != null)
            {
                Wind8000mSpeed = data.At8000m.Speed;
                Wind8000mDirection = data.At8000m.Direction;
            }

            if (data.GroundTemp.HasValue)
                GroundTempC = data.GroundTemp.Value;

            // Mission info
            if (data.Mission != null)
            {
                MissionTheatre = data.Mission.Theatre;
                MissionSortie = SanitizeSortieValue(data.Mission.Sortie);
                MissionStartTime = FormatStartTime(data.Mission.StartTime);

                AutoSelectMapFromTheatre(MissionTheatre);
            }
            else
            {
                MissionTheatre = null;
                MissionSortie = null;
                MissionStartTime = null;
                IsMapMatched = true;
            }

            DataSource = data.Source ?? "unknown";

            if (data.Timestamp > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(data.Timestamp);
                DataTimestamp = dt.ToString("yyyy-MM-dd HH:mm:ss UTC");
            }
            else
            {
                DataTimestamp = null;
            }
        }
        finally
        {
            _batchUpdating = false;
        }

        if (AutoUpdate)
            Calculate();
    }

    private static string? FormatStartTime(int? startTimeSeconds)
    {
        if (!startTimeSeconds.HasValue) return null;

        var s = startTimeSeconds.Value;
        var h = s / 3600;
        var m = (s % 3600) / 60;
        var sec = s % 60;
        return $"{h:D2}:{m:D2}:{sec:D2}";
    }

    private static string? SanitizeSortieValue(string? sortie)
    {
        if (string.IsNullOrWhiteSpace(sortie))
            return null;

        // Check if it's an unresolved DictKey (e.g., "DictKey_sortie_5")
        if (sortie.StartsWith("DictKey_", StringComparison.OrdinalIgnoreCase))
        {
            // Try to extract the number from patterns like "DictKey_sortie_5" or "DictKey_sortie_10"
            var parts = sortie.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[^1], out var sortieNumber))
            {
                return $"Sortie {sortieNumber}";
            }
            
            // If we can't parse it, return null to show "Not available"
            return null;
        }

        // Return the original value if it's not a DictKey
        return sortie;
    }

    private void AutoSelectMapFromTheatre(string? theatre)
    {
        if (string.IsNullOrWhiteSpace(theatre))
        {
            IsMapMatched = true;
            return;
        }

        var mapped = WindRecalculator.MapTheatreToMapName(theatre);
        if (!string.IsNullOrWhiteSpace(mapped) && AvailableMaps.Contains(mapped))
        {
            SelectedMap = mapped;
            IsMapMatched = true;
        }
        else
        {
            IsMapMatched = false;
        }
    }

    private void MaybeAutoCalculate()
    {
        if (_initializing) return;
        if (_batchUpdating) return;
        if (!AutoUpdate) return;

        Calculate();
    }

    [RelayCommand]
    private void Calculate()
    {
        try
        {
            var input = new WindRecalculator.BriefingInput(
                new WindRecalculator.WindLayer(GroundWindSpeed ?? 0, GroundWindDirection ?? 0),
                new WindRecalculator.WindLayer(Wind2000mSpeed ?? 0, Wind2000mDirection ?? 0),
                new WindRecalculator.WindLayer(Wind8000mSpeed ?? 0, Wind8000mDirection ?? 0),
                GroundTempC ?? 15,
                SelectedMap
            );

            var calculated = WindRecalculator.Compute(input);

            Results.Clear();
            foreach (var r in calculated)
                Results.Add(new CduWindLineViewModel(r));

            RaiseCanSendToCdu();
        }
        catch (Exception ex)
        {
            // Keep UI stable; optionally surface to a status field later
            System.Diagnostics.Debug.WriteLine($"Calculation error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearInputs()
    {
        _batchUpdating = true;
        try
        {
            GroundTempC = 0;
            GroundWindSpeed = 0;
            GroundWindDirection = 0;
            Wind2000mSpeed = 0;
            Wind2000mDirection = 0;
            Wind8000mSpeed = 0;
            Wind8000mDirection = 0;
            
            // Clear mission information
            MissionTheatre = null;
            MissionSortie = null;
            MissionStartTime = null;
            DataSource = null;
            DataTimestamp = null;
            
            // Clear CDU wind lines
            Results.Clear();
            RaiseCanSendToCdu();
        }
        finally
        {
            _batchUpdating = false;
        }

        MaybeAutoCalculate();
    }

    [RelayCommand]
    private async Task BrowseExportPath()
    {
        try
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null) return;

            var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select DCS Wind Data JSON File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON Files") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                },
                SuggestedStartLocation = await GetSuggestedStartLocation(mainWindow)
            });

            if (files.Count == 0) return;
            if (files[0] is not IStorageFile file) return;

            var path = file.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            ExportFilePath = path; // triggers SaveConfig via partial
            _dcsDataService.ExportFilePath = ExportFilePath;

            if (ConnectionMode == ConnectionMode.FileBased)
            {
                _dcsDataService.StartWatching();

                if (File.Exists(ExportFilePath))
                {
                    // optional: read once immediately
                    await _dcsDataService.ReadAndNotifyAsync();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BrowseExportPath error: {ex.Message}");
        }
    }

    private static async Task<IStorageFolder?> GetSuggestedStartLocation(Avalonia.Controls.Window mainWindow)
    {
        try
        {
            var defaultPath = DcsDataService.GetDefaultExportPath();
            var dir = Path.GetDirectoryName(defaultPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return await mainWindow.StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    [RelayCommand]
    private async Task SendToCdu()
    {
        if (!CanSendToCdu || IsSendingToCdu) return;

        IsSendingToCdu = true;
        CduSendProgress = "Initializing...";
        CduDebugLog.Clear();
        AddDebugLog("Starting CDU button sequence");

        _cduSendCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cduSendCancellationTokenSource.Token;

        var progress = new Progress<string>(message =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                CduSendProgress = message;
                AddDebugLog(message);
            });
        });

        try
        {
            System.Diagnostics.Debug.WriteLine("SendToCdu: Starting CDU button sequence");

            // Generate the complete sequence (page & height setup + wind data + temperature)
            var windLines = Results.ToArray();
            var sequence = _cduButtonSequence.GenerateCompleteSequence(windLines);
            
            AddDebugLog($"Generated sequence with {sequence.Count} commands");

            // Execute the sequence
            var success = await _cduButtonSequence.ExecuteSequenceAsync(sequence, progress, cancellationToken);

            if (success)
            {
                CduSendProgress = "Completed successfully";
                AddDebugLog("Sequence completed successfully");
                System.Diagnostics.Debug.WriteLine("SendToCdu: Successfully executed CDU button sequence");
            }
            else
            {
                CduSendProgress = "Failed";
                AddDebugLog("Sequence execution failed");
                System.Diagnostics.Debug.WriteLine("SendToCdu: Failed to execute CDU button sequence");
            }
        }
        catch (OperationCanceledException)
        {
            CduSendProgress = "Cancelled";
            AddDebugLog("Sequence cancelled by user");
            System.Diagnostics.Debug.WriteLine("SendToCdu: Cancelled by user");
        }
        catch (Exception ex)
        {
            CduSendProgress = $"Error: {ex.Message}";
            AddDebugLog($"Error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"SendToCdu error: {ex.Message}");
        }
        finally
        {
            IsSendingToCdu = false;
            _cduSendCancellationTokenSource?.Dispose();
            _cduSendCancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void CancelSendToCdu()
    {
        if (_cduSendCancellationTokenSource != null && !_cduSendCancellationTokenSource.Token.IsCancellationRequested)
        {
            AddDebugLog("Cancellation requested...");
            _cduSendCancellationTokenSource.Cancel();
        }
    }

    private void AddDebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logEntry = $"[{timestamp}] {message}";
        
        Dispatcher.UIThread.Post(() =>
        {
            CduDebugLog.Add(logEntry);
            // Keep only last 100 entries to prevent memory issues
            if (CduDebugLog.Count > 100)
            {
                CduDebugLog.RemoveAt(0);
            }
        });
    }

    [RelayCommand]
    private void SetConnectionMode(string modeString)
    {
        if (!Enum.TryParse<ConnectionMode>(modeString, out var mode))
            return;

        ConnectionMode = mode;
    }

    [RelayCommand]
    private void StartTcpServer()
    {
        if (ConnectionMode != ConnectionMode.TcpSocket) return;

        StartTcpServerInternal();
        SaveConfig();
    }

    [RelayCommand]
    private void StopTcpServer()
    {
        StopTcpServerInternal();
        SaveConfig();
    }

    [RelayCommand]
    private void ToggleTcpServer()
    {
        if (IsTcpServerRunning)
        {
            StopTcpServer();
        }
        else
        {
            StartTcpServer();
        }
    }

    private void StartTcpServerInternal()
    {
        if (IsTcpServerRunning)
        {
            System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal: Already running, IsListening={_dcsSocketService.IsListening}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal: Starting server on port {TcpPort}, IsListening before={_dcsSocketService.IsListening}");

        try
        {
            ListenerError = null;

            _dcsSocketService.StartListening(TcpPort);
            
            // Verify the server actually started before updating UI state
            var isListening = _dcsSocketService.IsListening;
            System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal: After StartListening, IsListening={isListening}");
            
            if (isListening)
            {
                IsTcpServerRunning = true;
                UpdateOverallConnectionFlags();
                RaiseCanSendToCdu();
                System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal: Successfully started, IsTcpServerRunning={IsTcpServerRunning}");
            }
            else
            {
                // Server didn't start even though StartListening didn't throw
                IsTcpServerRunning = false;
                UpdateOverallConnectionFlags();
                RaiseCanSendToCdu();
                System.Diagnostics.Debug.WriteLine("StartTcpServerInternal: StartListening returned but server is not listening");
            }
        }
        catch (Exception ex)
        {
            // Check if server actually started despite the exception
            var isListeningAfterException = _dcsSocketService.IsListening;
            System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal: Exception caught: {ex.GetType().Name}: {ex.Message}, IsListening after exception={isListeningAfterException}");
            
            if (isListeningAfterException)
            {
                // Server started but exception was thrown - sync UI state
                System.Diagnostics.Debug.WriteLine("StartTcpServerInternal: Server is listening despite exception, syncing UI state");
                IsTcpServerRunning = true;
                UpdateOverallConnectionFlags();
                RaiseCanSendToCdu();
            }
            else
            {
                IsTcpServerRunning = false;
                UpdateOverallConnectionFlags();
                RaiseCanSendToCdu();
            }

            // ListenerError is raised by the service; keep ex for debug only
            System.Diagnostics.Debug.WriteLine($"StartTcpServerInternal exception details: {ex}");
        }
    }

    private void StopTcpServerInternal()
    {
        try
        {
            _dcsSocketService.StopListening();
        }
        catch
        {
            // ignore on shutdown
        }
        finally
        {
            IsTcpServerRunning = false;
            UpdateOverallConnectionFlags();
            RaiseCanSendToCdu();
        }
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.MainWindow;
    }

    // ----------------------------
    // Property change hooks
    // ----------------------------

    partial void OnExportFilePathChanged(string value)
    {
        if (_initializing) return;

        if (ConnectionMode == ConnectionMode.FileBased)
        {
            _dcsDataService.ExportFilePath = value;
            _dcsDataService.StartWatching();
            UpdateOverallConnectionFlags();
        }

        SaveConfig();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        if (_initializing) return;
        SaveConfig();
    }

    partial void OnTcpPortChanged(int value)
    {
        if (_initializing) return;

        if (ConnectionMode == ConnectionMode.TcpSocket && IsTcpServerRunning)
        {
            // restart on new port
            StopTcpServerInternal();
            StartTcpServerInternal();
        }

        SaveConfig();
    }

    partial void OnConnectionModeChanged(ConnectionMode value)
    {
        if (_initializing) return;

        ApplyConnectionMode();
        SaveConfig();
    }

    partial void OnSelectedMapChanged(string value) => MaybeAutoCalculate();
    partial void OnGroundTempCChanged(int? value) => MaybeAutoCalculate();
    partial void OnGroundWindSpeedChanged(double? value) => MaybeAutoCalculate();
    partial void OnGroundWindDirectionChanged(double? value) => MaybeAutoCalculate();
    partial void OnWind2000mSpeedChanged(double? value) => MaybeAutoCalculate();
    partial void OnWind2000mDirectionChanged(double? value) => MaybeAutoCalculate();
    partial void OnWind8000mSpeedChanged(double? value) => MaybeAutoCalculate();
    partial void OnWind8000mDirectionChanged(double? value) => MaybeAutoCalculate();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _dcsDataService.DataUpdated -= OnDcsDataUpdated;
            _dcsDataService.ConnectionStatusChanged -= OnFileConnectionStatusChanged;

            _dcsSocketService.DataReceived -= OnDcsDataUpdated;
            _dcsSocketService.ConnectionStatusChanged -= OnTcpConnectionStatusChanged;
            _dcsSocketService.ListenerError -= OnListenerError;

            _dcsDataService.Dispose();
            _dcsSocketService.Dispose();
            _dcsBiosService.Dispose();
        }
        catch
        {
            // ignore during shutdown
        }
    }
}

public class CduWindLineViewModel : ObservableObject
{
    public int AltKft { get; }
    public int BrgDegMag { get; }
    public int SpdKt { get; }
    public int TmpC { get; }
    public string AltText { get; }
    public string BrgText { get; }
    public string SpdText { get; }
    public string TmpText { get; }
    public string BrgPlusSpd { get; }

    public CduWindLineViewModel(WindRecalculator.CduWindLine line)
    {
        AltKft = line.AltKft;
        BrgDegMag = line.BrgDegMag;
        SpdKt = line.SpdKt;
        TmpC = line.TmpC;
        AltText = line.AltText;
        BrgText = line.BrgText;
        SpdText = line.SpdText;
        TmpText = line.TmpText;
        BrgPlusSpd = line.BrgPlusSpd;
    }
}
