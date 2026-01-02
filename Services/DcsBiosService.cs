using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LASTE_Mate.Services;

public sealed class DcsBiosService : IDisposable
{
    private const int DefaultSendPort = 7778;
    private const int DefaultReceivePort = 7777;
    private static readonly IPAddress DefaultHost = IPAddress.Parse("127.0.0.1");

    private readonly object _sync = new();
    private UdpClient? _sendClient;
    private UdpClient? _receiveClient;
    private CancellationTokenSource? _receiveCancellationTokenSource;
    private Task? _receiveTask;
    private bool _disposed;

    private readonly Dictionary<string, string> _biosData = new();
    private readonly object _dataSync = new();

    public int SendPort { get; private set; } = DefaultSendPort;
    public int ReceivePort { get; private set; } = DefaultReceivePort;
    public IPAddress Host { get; private set; } = DefaultHost;

    public event EventHandler<string>? DataReceived;

    public DcsBiosService()
    {
        StartReceiving();
    }

    public DcsBiosService(IPAddress host, int sendPort, int receivePort = DefaultReceivePort)
    {
        Host = host;
        SendPort = sendPort;
        ReceivePort = receivePort;
        StartReceiving();
    }

    private void StartReceiving()
    {
        lock (_sync)
        {
            if (_receiveClient != null || _disposed) return;

            try
            {
                _receiveClient = new UdpClient(ReceivePort);
                _receiveCancellationTokenSource = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoop(_receiveCancellationTokenSource.Token));
                System.Diagnostics.Debug.WriteLine($"DcsBiosService: Started receiving on port {ReceivePort}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DcsBiosService: Failed to start receiving: {ex.Message}");
                _receiveClient?.Dispose();
                _receiveClient = null;
            }
        }
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _receiveClient != null)
        {
            try
            {
                var result = await _receiveClient.ReceiveAsync();
                var message = Encoding.ASCII.GetString(result.Buffer);
                
                lock (_dataSync)
                {
                    // Parse DCS-BIOS data format: "CONTROL VALUE\n"
                    var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            var control = parts[0];
                            var value = parts[1];
                            _biosData[control] = value;
                            
                            if (control == "CDU_LINE9")
                            {
                                DataReceived?.Invoke(this, value);
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DcsBiosService: Receive error: {ex.Message}");
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Gets the current value of a DCS-BIOS control.
    /// </summary>
    public string? GetControlValue(string control)
    {
        lock (_dataSync)
        {
            return _biosData.TryGetValue(control, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Checks if CDU_LINE9 contains an error message.
    /// </summary>
    public bool HasCduError()
    {
        var line9 = GetControlValue("CDU_LINE9");
        if (string.IsNullOrEmpty(line9)) return false;
        
        // Check for common error patterns (case-insensitive)
        var upper = line9.ToUpperInvariant();
        return upper.Contains("INPUT ERROR") || 
               upper.Contains("ERROR") || 
               upper.Contains("INVALID");
    }

    /// <summary>
    /// Sends a DCS-BIOS control command in the format: "CONTROL VALUE\n"
    /// </summary>
    public async Task<bool> SendControlAsync(string control, int value)
    {
        ThrowIfDisposed();

        try
        {
            var message = $"{control} {value}\n";
            var bytes = Encoding.ASCII.GetBytes(message);

            lock (_sync)
            {
                if (_sendClient == null)
                {
                    _sendClient = new UdpClient();
                }
            }

            var endpoint = new IPEndPoint(Host, SendPort);
            await _sendClient.SendAsync(bytes, bytes.Length, endpoint);

            System.Diagnostics.Debug.WriteLine($"DcsBiosService: Sent {control} {value} to {Host}:{SendPort}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DcsBiosService: Error sending {control} {value}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends a CLR command to clear CDU errors.
    /// </summary>
    public async Task<bool> ClearCduErrorAsync()
    {
        System.Diagnostics.Debug.WriteLine("DcsBiosService: Clearing CDU error with CLR");
        var pressed = await SendControlAsync("CDU_CLR", 1);
        if (!pressed) return false;
        
        await Task.Delay(50);
        
        return await SendControlAsync("CDU_CLR", 0);
    }

    /// <summary>
    /// Sends a press command (value=1) followed by a release command (value=0) after the specified delay.
    /// </summary>
    public async Task<bool> PressAndReleaseAsync(string control, int holdMs = 50)
    {
        var pressed = await SendControlAsync(control, 1);
        if (!pressed) return false;

        await Task.Delay(holdMs);

        return await SendControlAsync(control, 0);
    }

    /// <summary>
    /// Handles the CDU PAGE rocker switch (3-position maintained switch).
    /// Sets to targetPosition (0 or 2), holds for holdMs, then returns to center (1).
    /// </summary>
    public async Task<bool> SetPageRockerAsync(int targetPosition, int holdMs = 300)
    {
        if (targetPosition != 0 && targetPosition != 2)
        {
            System.Diagnostics.Debug.WriteLine($"DcsBiosService: Invalid PAGE rocker position {targetPosition}, must be 0 or 2");
            return false;
        }

        // Set to target position
        var set = await SendControlAsync("CDU_PG", targetPosition);
        if (!set) return false;

        // Hold
        await Task.Delay(holdMs);

        // Return to center
        return await SendControlAsync("CDU_PG", 1);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DcsBiosService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync)
        {
            try
            {
                _receiveCancellationTokenSource?.Cancel();
                
                _receiveClient?.Close();
                _receiveClient?.Dispose();
                
                _sendClient?.Close();
                _sendClient?.Dispose();
                
                if (_receiveTask != null)
                {
                    try
                    {
                        _receiveTask.Wait(TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore during shutdown
            }
            finally
            {
                _receiveClient = null;
                _sendClient = null;
                _receiveTask = null;
                _receiveCancellationTokenSource?.Dispose();
                _receiveCancellationTokenSource = null;
            }
        }
    }
}

