using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleTcp;
using LASTE_Mate.Models;

namespace LASTE_Mate.Services;

public sealed class DcsSocketService : IDisposable
{
    private const int DefaultPort = 10309;
    private static readonly IPAddress BindAddress = IPAddress.Parse("127.0.0.1");

    private readonly object _sync = new();
    private readonly int _instanceId;

    private SimpleTcpServer? _server;
    private readonly Dictionary<string, StringBuilder> _rxBuffersByClient = new();

    private bool _disposed;

    private DateTime _lastDataReceivedUtc = DateTime.MinValue;
    private const int DataFreshSeconds = 5;

    private bool _lastReportedConnected;

    public DcsSocketService()
    {
        _instanceId = GetHashCode();
        Console.WriteLine($"[DcsSocketService] Singleton instance created: Id={_instanceId:X8}, HashCode={GetHashCode():X8}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never // Always include properties, even if null/empty
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
    public event Action<bool>? ListeningStatusChanged; // New event for listening state changes

    /// <summary>
    /// Starts the TCP server asynchronously. Idempotent and thread-safe:
    /// - If already listening on the same port: does nothing.
    /// - If listening on different port: stops then starts on requested port (atomic).
    /// This method should be called from a background thread (e.g., Task.Run) to avoid blocking the UI.
    /// </summary>
    public async Task StartListeningAsync(int port = DefaultPort)
    {
        ThrowIfDisposed();

        // Run the actual start logic in a background task to avoid blocking
        await Task.Run(() =>
        {
            lock (_sync)
            {
                // Already listening on the requested port -> no-op
                if (_server is not null && _server.IsListening && Port == port)
                {
                    Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Already listening on port {port}, no-op");
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    return;
                }

                Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Starting listener on port {port}");

                // If a server exists (listening or not), stop it first
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

                    // Reset state
                    _rxBuffersByClient.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Successfully started listening on {endpoint}");
                    RaiseConnectionStatusIfChanged_NoLock(); // will be false until client/data arrives
                    RaiseListeningStatusChanged_NoLock(); // notify UI that listening state changed
                }
                catch (Exception ex)
                {
                    _server = null;
                    _rxBuffersByClient.Clear();
                    _lastDataReceivedUtc = DateTime.MinValue;

                    var errorMsg = $"Failed to bind {endpoint}: {ex.Message}";
                    Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] {errorMsg}");
                    ListenerError?.Invoke(errorMsg);
                    RaiseConnectionStatusIfChanged_NoLock();
                    RaiseListeningStatusChanged_NoLock();
                    throw;
                }
            }
        });
    }

    /// <summary>
    /// Synchronous version for backward compatibility. Internally calls StartListeningAsync.
    /// </summary>
    public void StartListening(int port = DefaultPort)
    {
        // Start asynchronously but don't wait - fire and forget
        // The UI will be updated via ListeningStatusChanged event
        _ = Task.Run(async () =>
        {
            try
            {
                await StartListeningAsync(port);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] StartListening background task failed: {ex.Message}");
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
                Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] StopListening called but not listening, no-op");
                return;
            }

            Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Stopping listener on port {Port}");
            StopListening_NoLock();
            RaiseConnectionStatusIfChanged_NoLock();
            RaiseListeningStatusChanged_NoLock();
            Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Listener stopped");
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
        // SimpleTcp calls this potentially on a background thread
        // Keep critical section small but consistent
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

            // parse newline-delimited JSON (one message per line)
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
                    }
                }
                catch (JsonException)
                {
                    // ignore malformed/partial lines (but with newline framing, this should be rare)
                }
            }

            RaiseConnectionStatusIfChanged_NoLock();
        }

        // Invoke outside lock
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

    public async Task<bool> SendCommandAsync(DcsCommand command)
    {
        ThrowIfDisposed();

        SimpleTcpServer? server;
        List<string> clients;

        lock (_sync)
        {
            server = _server;
            if (server is null || !server.IsListening)
                return false;

            clients = server.GetClients().ToList();
            if (clients.Count == 0)
                return false;
        }

        try
        {
            command.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            // Debug: Log the command before serialization
            Console.WriteLine($"SendCommandAsync: Command type={command.GetType().Name}, Type={command.Type}");
            if (command is ButtonPressCommand btnCmd)
            {
                Console.WriteLine($"SendCommandAsync: ButtonPressCommand - Button={btnCmd.Button}, DeviceId={btnCmd.DeviceId}, ActionId={btnCmd.ActionId}");
            }
            
            // Use a custom serializer that explicitly includes all properties
            // Don't use PropertyNamingPolicy for commands since we use explicit JsonPropertyName attributes
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never // Include all properties, even empty strings
            };
            
            var json = JsonSerializer.Serialize(command, command.GetType(), jsonOptions) + "\n";
            Console.WriteLine($"SendCommandAsync: Serialized JSON: {json.Trim()}");
            
            var bytes = Encoding.UTF8.GetBytes(json);

            foreach (var c in clients)
                await server.SendAsync(c, bytes);

            return true;
        }
        catch
        {
            return false;
        }
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
        Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Connection status changed: {wasConnected} -> {now} (data age: {dataAge:F1}s, listening: {_server?.IsListening ?? false}, clients: {SafeClientCount()})");
        
        ConnectionStatusChanged?.Invoke(this, now);
    }

    private void RaiseListeningStatusChanged_NoLock()
    {
        var isListening = _server is not null && _server.IsListening;
        Console.WriteLine($"[DcsSocketService:{_instanceId:X8}] Listening status changed: IsListening={isListening}");
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
