using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LASTE_Mate.Core;
using LASTE_Mate.Models;
using LASTE_Mate.Services;
using NLog;

namespace LASTE_Mate.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DcsSocketService _dcsSocketService;
    private readonly DcsBiosService _dcsBiosService;
    private readonly CduButtonSequence _cduButtonSequence;
    private readonly AppConfigService _configService;

    private bool _initializing = true;
    private bool _batchUpdating;
    private bool _disposed;
    private bool _isApplyingConnectionState; // Guard to prevent concurrent ApplyConnectionState calls

    [ObservableProperty] private ObservableCollection<string> _availableMaps;
    [ObservableProperty] private string _selectedMap = "Caucasus";

    [ObservableProperty] private int? _groundTempC = 15;

    [ObservableProperty] private double? _groundWindSpeed = 0;
    [ObservableProperty] private double? _groundWindDirection = 0;

    [ObservableProperty] private double? _wind2000mSpeed = 0;
    [ObservableProperty] private double? _wind2000mDirection = 0;

    [ObservableProperty] private double? _wind8000mSpeed = 0;
    [ObservableProperty] private double? _wind8000mDirection = 0;

    [ObservableProperty] private bool _isDcsConnected;

    [ObservableProperty] private bool _autoUpdate = true;

    [ObservableProperty] private ObservableCollection<CduWindLineViewModel> _results = new();

    [ObservableProperty] private string? _missionTheatre;
    [ObservableProperty] private string? _missionSortie;
    [ObservableProperty] private string? _missionStartTime;

    [ObservableProperty] private string? _dataSource;
    [ObservableProperty] private string? _dataTimestamp;

    [ObservableProperty] private bool _isMapMatched = true;

    [ObservableProperty] private bool _isTcpConnected;
    [ObservableProperty] private int _tcpPort = 10309;

    [ObservableProperty] private bool _isTcpServerRunning;
    [ObservableProperty] private bool _tcpListenerEnabled;
    
    [ObservableProperty] private int _dcsBiosPort = 7778;

    [ObservableProperty] private string? _listenerError;

    [ObservableProperty] private bool _isSendingToCdu;
    partial void OnIsSendingToCduChanged(bool value) => RaiseCanSendToCdu();

    [ObservableProperty] private string _cduSendProgress = string.Empty;

    [ObservableProperty] private ObservableCollection<string> _cduDebugLog = new();

    [ObservableProperty] private bool _isDebugLogExpanded;
    [ObservableProperty] private bool _isSettingsExpanded = false;

    [ObservableProperty] private string _appVersion = VersionHelper.GetVersion();

    private CancellationTokenSource? _cduSendCancellationTokenSource;
    private System.Threading.Timer? _connectionStatusTimer;
    private static readonly ILogger Logger = LoggingService.GetLogger<MainWindowViewModel>();

    public MainWindowViewModel(
        DcsSocketService dcsSocketService,
        DcsBiosService dcsBiosService,
        AppConfigService configService)
    {
        _dcsSocketService = dcsSocketService;
        _dcsBiosService = dcsBiosService;
        _configService = configService;
        _cduButtonSequence = new CduButtonSequence(_dcsBiosService);

        _availableMaps = new ObservableCollection<string>(WindRecalculator.GetAvailableMaps());

        _dcsSocketService.DataReceived += OnDcsDataUpdated;
        _dcsSocketService.ConnectionStatusChanged += OnTcpConnectionStatusChanged;
        _dcsSocketService.ListenerError += OnListenerError;
        _dcsSocketService.ListeningStatusChanged += OnListeningStatusChanged;

        LoadConfig();
        ApplyConnectionState();

        var actualIsListening = _dcsSocketService.IsListening;
        if (IsTcpServerRunning != actualIsListening)
        {
            Logger.Debug("Initial sync IsTcpServerRunning: {Old} -> {New}", IsTcpServerRunning, actualIsListening);
            IsTcpServerRunning = actualIsListening;
        }

        _connectionStatusTimer = new System.Threading.Timer(OnConnectionStatusTimerTick, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        _initializing = false;
        
        Logger.Info("Instance created, DcsSocketService singleton instance: {HashCode:X8}", _dcsSocketService.GetHashCode());
    }

    public bool CanSendToCdu => Results.Count > 0;

    private void LoadConfig()
    {
        var config = _configService.GetConfig();

        _batchUpdating = true;
        try
        {
            TcpPort = config.TcpPort <= 0 ? 10309 : config.TcpPort;
            AutoUpdate = config.AutoUpdate;
            TcpListenerEnabled = config.TcpListenerEnabled;
            DcsBiosPort = config.DcsBiosPort <= 0 ? 7778 : config.DcsBiosPort;
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
            TcpPort = TcpPort,
            AutoUpdate = AutoUpdate,
            TcpListenerEnabled = TcpListenerEnabled,
            DcsBiosPort = DcsBiosPort
        };

        _configService.SaveConfig(config);
    }

    private void ApplyConnectionState()
    {
        if (_isApplyingConnectionState)
        {
            Logger.Debug("ApplyConnectionState: Already applying, skipping duplicate call");
            return;
        }

        _isApplyingConnectionState = true;
        try
        {
            Logger.Debug("ApplyConnectionState: TcpListenerEnabled={TcpListenerEnabled}, TcpPort={TcpPort}", TcpListenerEnabled, TcpPort);
            
            ListenerError = null;

            if (TcpListenerEnabled)
            {
                Logger.Info("Starting TCP listener on port {TcpPort} in background thread", TcpPort);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dcsSocketService.StartListeningAsync(TcpPort);
                        Logger.Debug("TCP listener start task completed");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to start TCP listener");
                    }
                });
            }
            else
            {
                Logger.Info("TcpListenerEnabled=false, stopping TCP listener");
                _dcsSocketService.StopListening();
                IsTcpServerRunning = false;
            }

            UpdateOverallConnectionFlags();
            RaiseCanSendToCdu();
        }
        finally
        {
            _isApplyingConnectionState = false;
        }
    }

    private void UpdateOverallConnectionFlags()
    {
        IsTcpConnected = _dcsSocketService.IsConnected;
        IsDcsConnected = IsTcpConnected;
    }

    private void RaiseCanSendToCdu()
    {
        OnPropertyChanged(nameof(CanSendToCdu));
    }

    private void OnTcpConnectionStatusChanged(object? sender, bool connected)
    {
        // Marshal to UI thread
        Dispatcher.UIThread.Post(() =>
        {
            IsTcpConnected = connected;
            IsDcsConnected = connected;
            RaiseCanSendToCdu();
        });
    }

    /// <summary>
    /// Timer callback to check for stale connection status and update UI accordingly.
    /// </summary>
    private void OnConnectionStatusTimerTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            // Sync TCP server running state with actual service state
            var actualIsListening = _dcsSocketService.IsListening;
            if (IsTcpServerRunning != actualIsListening)
            {
                Logger.Debug("Syncing IsTcpServerRunning: {Old} -> {New}", IsTcpServerRunning, actualIsListening);
                IsTcpServerRunning = actualIsListening;
            }

            // Recompute connection status based on current state
            var wasConnected = IsDcsConnected;
            var nowConnected = _dcsSocketService.IsConnected;
            
            if (wasConnected != nowConnected)
            {
                IsTcpConnected = nowConnected;
                IsDcsConnected = nowConnected;
                RaiseCanSendToCdu();
            }
        });
    }

    private void OnListenerError(string errorMessage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ListenerError = errorMessage;
        });
    }

    private void OnListeningStatusChanged(bool isListening)
    {
        // Marshal to UI thread and update the UI property
        Dispatcher.UIThread.Post(() =>
        {
            Logger.Debug("OnListeningStatusChanged: isListening={IsListening}, updating IsTcpServerRunning", isListening);
            IsTcpServerRunning = isListening;
        });
    }

    private void OnDcsDataUpdated(object? sender, DcsExportData? data)
    {
        if (data == null)
        {
            return;
        }

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
        {
            Calculate();
        }
    }

    private static string? FormatStartTime(int? startTimeSeconds)
    {
        if (!startTimeSeconds.HasValue)
        {
            return null;
        }

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
        if (_initializing)
        {
            return;
        }
        if (_batchUpdating)
        {
            return;
        }
        if (!AutoUpdate)
        {
            return;
        }

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
            Logger.Error(ex, "Calculation error");
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
    }


    [RelayCommand]
    private async Task SendToCdu()
    {
        if (!CanSendToCdu || IsSendingToCdu)
        {
            return;
        }

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
            Logger.Info("SendToCdu: Starting CDU button sequence");

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
                Logger.Info("SendToCdu: Successfully executed CDU button sequence");
            }
            else
            {
                CduSendProgress = "Failed";
                AddDebugLog("Sequence execution failed");
                Logger.Warn("SendToCdu: Failed to execute CDU button sequence");
            }
        }
        catch (OperationCanceledException)
        {
            CduSendProgress = "Cancelled";
            AddDebugLog("Sequence cancelled by user");
            Logger.Info("SendToCdu: Cancelled by user");
        }
        catch (Exception ex)
        {
            CduSendProgress = $"Error: {ex.Message}";
            AddDebugLog($"Error: {ex.Message}");
            Logger.Error(ex, "SendToCdu error");
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
    private void ToggleTcpServer()
    {
        TcpListenerEnabled = !TcpListenerEnabled;
        ApplyConnectionState();
        SaveConfig();
    }

    // ----------------------------
    // Property change hooks
    // ----------------------------

    partial void OnAutoUpdateChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }
        SaveConfig();
    }

    partial void OnTcpPortChanged(int value)
    {
        if (_initializing)
        {
            return;
        }

        if (TcpListenerEnabled)
        {
            // Restart listener on new port
            ApplyConnectionState();
        }

        SaveConfig();
    }

    partial void OnDcsBiosPortChanged(int value)
    {
        if (_initializing)
        {
            return;
        }
        SaveConfig();
    }

    partial void OnTcpListenerEnabledChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }
        ApplyConnectionState();
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Logger.Debug("Disposing...");

        try
        {
            // Stop timer
            _connectionStatusTimer?.Dispose();
            _connectionStatusTimer = null;

            // Unsubscribe from events
            _dcsSocketService.DataReceived -= OnDcsDataUpdated;
            _dcsSocketService.ConnectionStatusChanged -= OnTcpConnectionStatusChanged;
            _dcsSocketService.ListenerError -= OnListenerError;
            _dcsSocketService.ListeningStatusChanged -= OnListeningStatusChanged;

            // Stop TCP listener
            if (TcpListenerEnabled)
            {
                _dcsSocketService.StopListening();
            }

            // Cancel CDU send operation if running
            _cduSendCancellationTokenSource?.Cancel();
            _cduSendCancellationTokenSource?.Dispose();

            // Dispose services
            _dcsSocketService.Dispose();
            _dcsBiosService.Dispose();

            Logger.Debug("Disposed successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during disposal");
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
