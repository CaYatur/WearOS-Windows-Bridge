using InTheHand.Net.Sockets;

namespace Bridge.Windows;

/// <summary>
/// RFCOMM listener. The radio, the stack and the SDP record are all things that can be missing or
/// busy, so the listener rebuilds itself rather than dying on the first failure — otherwise
/// Bluetooth stays silently dead until the app is restarted.
/// </summary>
public sealed class BluetoothServer(BridgeConnectionHandler handler, ConnectionStatus status) : IDisposable
{
    public static readonly Guid ServiceId = Guid.Parse("7e3d7b5a-3c51-4a32-93ab-c854b152e743");
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private BluetoothListener? _listener;
    private volatile bool _disposed;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                _listener = new BluetoothListener(ServiceId) { ServiceName = "WearOS Windows Bridge" };
                _listener.Start();
                Log.Info($"Bluetooth RFCOMM listener started for service {ServiceId}");
                await AcceptLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Typically no radio, radio switched off, or the stack is not the Microsoft one.
                Log.Warn($"Bluetooth listener stopped: {ex.Message}. Retrying in {RetryDelay.TotalSeconds:0}s");
            }
            finally { try { _listener?.Stop(); } catch { } _listener = null; }

            if (ct.IsCancellationRequested || _disposed) break;
            try { await Task.Delay(RetryDelay, ct); } catch { break; }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var listener = _listener ?? throw new InvalidOperationException("listener disposed");
            // AcceptBluetoothClient blocks; Task.Run keeps the accept off the async path so
            // cancellation can still unwind promptly.
            var client = await Task.Run(listener.AcceptBluetoothClient, ct);
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(BluetoothClient client, CancellationToken ct)
    {
        using (client)
        {
            var peer = SafeName(client);
            Log.Info($"Bluetooth client connected: {peer}");
            status.SetConnected("Bluetooth", true);
            try { await handler.HandleAsync(client.GetStream(), "Bluetooth", ct); }
            catch (Exception ex) { Log.Info($"Bluetooth client {peer} disconnected: {ex.Message}"); }
            finally { status.SetConnected("Bluetooth", false); Log.Info($"Bluetooth client disconnected: {peer}"); }
        }
    }

    private static string SafeName(BluetoothClient client)
    {
        try { return client.RemoteMachineName ?? "unknown"; } catch { return "unknown"; }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _listener?.Stop(); } catch { }
    }
}
