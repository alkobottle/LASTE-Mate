using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LASTE_Mate.Models;
using LASTE_Mate.Serialization;
using NLog;

namespace LASTE_Mate.Services;

public sealed class DcsSocketService : IDisposable
{
    private const int DefaultPort = 10309;
    private static readonly IPAddress BindAddress = IPAddress.Parse("127.0.0.1");

    private readonly Lock _sync = new();
    private static readonly ILogger Logger = LoggingService.GetLogger<DcsSocketService>();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;

    private sealed class ClientState(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public StringBuilder Buffer { get; } = new(4096);
    }

    private readonly Dictionary<string, ClientState> _clients = new();

    private bool _disposed;

    private DateTime _lastDataReceivedUtc = DateTime.MinValue;
    private const int DataFreshSeconds = 5;

    private bool _lastReportedConnected;

    public DcsSocketService()
    {
        var instanceId = GetHashCode();
        Logger.Info("Singleton instance created: Id={InstanceId:X8}, HashCode={HashCode:X8}", instanceId, GetHashCode());
    }

    private static readonly DcsJsonContext JsonContext = DcsJsonContext.Default;

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
                return _listener is not null;
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
                return _listener is not null && SafeClientCount() > 0;
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
                if (_listener is null)
                {
                    return false;
                }

                if (SafeClientCount() == 0)
                {
                    return false;
                }

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

        await Task.Run(async () =>
        {
            lock (_sync)
            {
                if (_listener is not null && Port == port)
                {
                    Logger.Debug("Already listening on port {Port}, no-op", port);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    return;
                }

                Logger.Info("Starting listener on port {Port}", port);

                StopListening_NoLock();

                Port = port;
                _listenerCts = new CancellationTokenSource();

                var endpoint = $"{BindAddress}:{port}";
                try
                {
                    _listener = new TcpListener(BindAddress, port);
                    _listener.Start();

                    _clients.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_listener, _listenerCts.Token));

                    Logger.Info("Successfully started listening on {Endpoint}", endpoint);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                }
                catch (Exception ex)
                {
                    _listener = null;
                    _clients.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    var errorMsg = $"Failed to bind {endpoint}: {ex.Message}";
                    Logger.Error(ex, "Failed to bind {Endpoint}", endpoint);
                    ListenerError?.Invoke(errorMsg);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    throw;
                }
            }

            // Ensure accept loop started before returning
            if (_acceptLoopTask is not null)
            {
                await Task.Yield();
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
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_listener is null)
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
        if (_listener is null)
        {
            return;
        }

        try
        {
            _listenerCts?.Cancel();

            try
            {
                _listener.Stop();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Listener.Stop threw during shutdown, ignoring");
            }

            if (_acceptLoopTask is not null)
            {
                try { _acceptLoopTask.Wait(TimeSpan.FromSeconds(1)); }
                catch (Exception ex) { Logger.Debug(ex, "Accept loop wait failed during shutdown"); }
            }

            foreach (var client in _clients.Values.ToList())
            {
                try { client.Client.Close(); }
                catch (Exception ex) { Logger.Debug(ex, "Client close threw during shutdown"); }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "StopListening_NoLock encountered an error during shutdown; ignoring because we are disposing.");
        }
        finally
        {
            _listener = null;
            _listenerCts?.Dispose();
            _listenerCts = null;
            _acceptLoopTask = null;
            _clients.Clear();
            _lastDataReceivedUtc = DateTime.MinValue;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    Logger.Error(ex, "Accept loop error");
                    continue;
                }

                var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
                var state = new ClientState(client);

                lock (_sync)
                {
                    _clients[clientId] = state;
                    RaiseConnectionStatusIfChanged_NoLock();
                }

                Logger.Debug("Client connected: {ClientId}", clientId);

                await Task.Run(() => HandleClientAsync(clientId, state, token), token);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Logger.Error(ex, "Unhandled exception in accept loop");
            }
        }
    }

    private async Task HandleClientAsync(string clientId, ClientState state, CancellationToken token)
    {
        var client = state.Client;
        await using var stream = client.GetStream();
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await ReadFromClientAsync(stream, buffer, clientId, token);
                if (!bytesRead.HasValue || bytesRead.Value == 0)
                {
                    // canceled, errored, or client closed
                    break;
                }

                var parsed = ParseChunk(state, buffer, bytesRead.Value);
                DispatchParsed(parsed);
            }
        }
        finally
        {
            lock (_sync)
            {
                _clients.Remove(clientId);
                RaiseConnectionStatusIfChanged_NoLock();
            }

            try
            {
                client.Close();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Client close threw during disconnect cleanup");
            }

            Logger.Debug("Client disconnected: {ClientId}", clientId);
        }
    }

    private static async Task<int?> ReadFromClientAsync(NetworkStream stream, byte[] buffer, string clientId, CancellationToken token)
    {
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Receive error from {ClientId}", clientId);
            return null;
        }
    }

    private List<DcsExportData> ParseChunk(ClientState state, byte[] buffer, int bytesRead)
    {
        var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var parsed = new List<DcsExportData>();

        lock (_sync)
        {
            var sb = state.Buffer;
            sb.Append(chunk);

            while (true)
            {
                var line = ExtractLine_NoLock(sb);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var data = JsonSerializer.Deserialize(line, JsonContext.DcsExportData);
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

        return parsed;
    }

    private void DispatchParsed(IEnumerable<DcsExportData> parsed)
    {
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
        for (var i = 0; i < sb.Length; i++)
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
        try { return _clients.Count; }
        catch { return 0; }
    }

    private void RaiseConnectionStatusIfChanged_NoLock()
    {
        var now = IsConnected;
        if (now == _lastReportedConnected)
        {
            return;
        }

        var wasConnected = _lastReportedConnected;
        _lastReportedConnected = now;

        var dataAge = (DateTime.UtcNow - _lastDataReceivedUtc).TotalSeconds;
        Logger.Debug("Connection status changed: {WasConnected} -> {Now} (data age: {DataAge:F1}s, listening: {IsListening}, clients: {ClientCount})",
            wasConnected, now, dataAge, _listener is not null, SafeClientCount());

        ConnectionStatusChanged?.Invoke(this, now);
    }

    private void RaiseListeningStatusChanged_NoLock()
    {
        var isListening = _listener is not null;
        Logger.Debug("Listening status changed: IsListening={IsListening}", isListening);
        ListeningStatusChanged?.Invoke(isListening);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DcsSocketService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_sync)
        {
            StopListening_NoLock();
        }
    }
}
