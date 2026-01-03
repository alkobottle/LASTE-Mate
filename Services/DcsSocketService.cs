using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleTcp;
using LASTE_Mate.Models;
using NLog;

namespace LASTE_Mate.Services;

public sealed class DcsSocketService : IDisposable
{
    private const int DefaultPort = 10309;
    private static readonly IPAddress BindAddress = IPAddress.Parse("127.0.0.1");

    private readonly object _sync = new();
    private readonly int _instanceId;
    private static readonly ILogger Logger = LoggingService.GetLogger<DcsSocketService>();

    private SimpleTcpServer? _server;
    private readonly Dictionary<string, StringBuilder> _rxBuffersByClient = new();

    private bool _disposed;

    private DateTime _lastDataReceivedUtc = DateTime.MinValue;
    private const int DataFreshSeconds = 5;

    private bool _lastReportedConnected;

    public DcsSocketService()
    {
        _instanceId = GetHashCode();
        Logger.Info("Singleton instance created: Id={InstanceId:X8}, HashCode={HashCode:X8}", _instanceId, GetHashCode());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public int Port { get; private set; } = DefaultPort;

    /// <summary>
    /// True if the TCP server socket is currently listening on 127.0.0.1:Port.
    /// </summary>
    public bool IsListening
    {
        get
        {
            lock (_sync)
            {
                return _server is not null && _server.IsListening;
            }
        }
    }

    /// <summary>
    /// True if at least one client is connected.
    /// </summary>
    public bool HasClient
    {
        get
        {
            lock (_sync)
            {
                return _server is not null && _server.IsListening && SafeClientCount() > 0;
            }
        }
    }

    /// <summary>
    /// “Connected” in UI terms: server is listening, at least one client exists,
    /// and we received data recently (within DataFreshSeconds).
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                if (_server is null || !_server.IsListening) return false;
                if (SafeClientCount() == 0) return false;
                return (DateTime.UtcNow - _lastDataReceivedUtc).TotalSeconds < DataFreshSeconds;
            }
        }
    }

    public event EventHandler<DcsExportData>? DataReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;
    public event Action<string>? ListenerError;
    public event Action<bool>? ListeningStatusChanged;

    /// <summary>
    /// Starts the TCP server asynchronously. Idempotent and thread-safe:
    /// - If already listening on the same port: does nothing.
    /// - If listening on different port: stops then starts on requested port (atomic).
    /// This method should be called from a background thread (e.g., Task.Run) to avoid blocking the UI.
    /// </summary>
    public async Task StartListeningAsync(int port = DefaultPort)
    {
        ThrowIfDisposed();

        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (_server is not null && _server.IsListening && Port == port)
                {
                    Logger.Debug("Already listening on port {Port}, no-op", port);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    return;
                }

                Logger.Info("Starting listener on port {Port}", port);

                StopListening_NoLock();

                Port = port;

                var endpoint = $"{BindAddress}:{port}";
                try
                {
                    var server = new SimpleTcpServer(endpoint);

                    server.Events.ClientConnected += OnClientConnected;
                    server.Events.ClientDisconnected += OnClientDisconnected;
                    server.Events.DataReceived += OnDataReceived;

                    server.Start();

                    if (!server.IsListening)
                    {
                        server.Dispose();
                        throw new InvalidOperationException("Server.Start() returned but server is not listening.");
                    }

                    _server = server;

                    _rxBuffersByClient.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    Logger.Info("Successfully started listening on {Endpoint}", endpoint);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                }
                catch (Exception ex)
                {
                    _server = null;
                    _rxBuffersByClient.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    var errorMsg = $"Failed to bind {endpoint}: {ex.Message}";
                    Logger.Error(ex, "Failed to bind {Endpoint}", endpoint);
                    ListenerError?.Invoke(errorMsg);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    throw;
                }
            }
        });
    }

    public void StartListening(int port = DefaultPort)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await StartListeningAsync(port);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StartListening background task failed");
            }
        });
    }

    /// <summary>
    /// Stops the TCP server if running. Safe to call multiple times.
    /// </summary>
    public void StopListening()
    {
        if (_disposed) return;

        lock (_sync)
        {
            if (_server is null || !_server.IsListening)
            {
                Logger.Debug("StopListening called but not listening, no-op");
                return;
            }

            Logger.Info("Stopping listener on port {Port}", Port);
            StopListening_NoLock();
            RaiseConnectionStatusIfChanged_NoLock();
            RaiseListeningStatusChanged_NoLock();
            Logger.Info("Listener stopped");
        }
    }

    private void StopListening_NoLock()
    {
        if (_server is null)
            return;

        try
        {
            _server.Events.ClientConnected -= OnClientConnected;
            _server.Events.ClientDisconnected -= OnClientDisconnected;
            _server.Events.DataReceived -= OnDataReceived;

            if (_server.IsListening)
                _server.Stop();

            _server.Dispose();
        }
        catch
        {
            // swallow: we’re shutting down
        }
        finally
        {
            _server = null;
            _rxBuffersByClient.Clear();
            _lastDataReceivedUtc = DateTime.MinValue;
        }
    }

    private void OnClientConnected(object? sender, ClientConnectedEventArgs e)
    {
        lock (_sync)
        {
            if (!_rxBuffersByClient.ContainsKey(e.IpPort))
                _rxBuffersByClient[e.IpPort] = new StringBuilder(4096);

            RaiseConnectionStatusIfChanged_NoLock();
        }
    }

    private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
    {
        lock (_sync)
        {
            _rxBuffersByClient.Remove(e.IpPort);
            RaiseConnectionStatusIfChanged_NoLock();
        }
    }

    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        List<DcsExportData> parsed = new();

        lock (_sync)
        {
            if (!_rxBuffersByClient.TryGetValue(e.IpPort, out var sb))
            {
                sb = new StringBuilder(4096);
                _rxBuffersByClient[e.IpPort] = sb;
            }

            var chunk = Encoding.UTF8.GetString(e.Data);
            sb.Append(chunk);

            while (true)
            {
                var line = ExtractLine_NoLock(sb);
                if (line is null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var data = JsonSerializer.Deserialize<DcsExportData>(line, JsonOptions);
                    if (data is not null)
                    {
                        parsed.Add(data);
                        _lastDataReceivedUtc = DateTime.UtcNow;
                        
                        Logger.Info("Received data from DCS script: Source={Source}, Mission={Mission}, Theatre={Theatre}, Sortie={Sortie}, Timestamp={Timestamp}",
                            data.Source ?? "unknown",
                            data.Mission?.Theatre ?? "N/A",
                            data.Mission?.Theatre ?? "N/A",
                            data.Mission?.Sortie ?? "N/A",
                            data.Timestamp);
                    }
                }
                catch (JsonException ex)
                {
                    Logger.Warn(ex, "Failed to parse JSON from script: {Line}", line.Length > 200 ? line.Substring(0, 200) + "..." : line);
                }
            }

            RaiseConnectionStatusIfChanged_NoLock();
        }

        foreach (var d in parsed)
        {
            DataReceived?.Invoke(this, d);
        }
    }

    /// <summary>
    /// Extracts a single line (ending with '\n') from the buffer, trimming '\r'.
    /// Returns null if no complete line is available yet.
    /// </summary>
    private static string? ExtractLine_NoLock(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                var line = sb.ToString(0, i);
                sb.Remove(0, i + 1);
                return line.TrimEnd('\r');
            }
        }
        return null;
    }

    private int SafeClientCount()
    {
        try { return _server?.GetClients().Count() ?? 0; }
        catch { return 0; }
    }

    private void RaiseConnectionStatusIfChanged_NoLock()
    {
        var now = IsConnected;
        if (now == _lastReportedConnected) return;

        var wasConnected = _lastReportedConnected;
        _lastReportedConnected = now;
        
        var dataAge = (DateTime.UtcNow - _lastDataReceivedUtc).TotalSeconds;
        Logger.Debug("Connection status changed: {WasConnected} -> {Now} (data age: {DataAge:F1}s, listening: {IsListening}, clients: {ClientCount})",
            wasConnected, now, dataAge, _server?.IsListening ?? false, SafeClientCount());
        
        ConnectionStatusChanged?.Invoke(this, now);
    }

    private void RaiseListeningStatusChanged_NoLock()
    {
        var isListening = _server is not null && _server.IsListening;
        Logger.Debug("Listening status changed: IsListening={IsListening}", isListening);
        ListeningStatusChanged?.Invoke(isListening);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DcsSocketService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync)
        {
            StopListening_NoLock();
        }
    }
}
