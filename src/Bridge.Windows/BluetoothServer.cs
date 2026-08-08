using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace Bridge.Windows;

public sealed class BluetoothServer(BridgeConnectionHandler handler) : IDisposable
{
    public static readonly Guid ServiceId = Guid.Parse("7e3d7b5a-3c51-4a32-93ab-c854b152e743");
    private BluetoothListener? _listener;

    public Task RunAsync(CancellationToken ct)
    {
        _listener = new BluetoothListener(ServiceId) { ServiceName = "WearOS Windows Bridge" };
        _listener.Start();
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                BluetoothClient? client = null;
                try
                {
                    client = await Task.Run(() => _listener.AcceptBluetoothClient(), ct);
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException) { client?.Dispose(); break; }
                catch (Exception ex) { client?.Dispose(); Console.Error.WriteLine($"Bluetooth accept failed: {ex.Message}"); await Task.Delay(1500, ct); }
            }
        }, ct);
    }

    private async Task HandleClientAsync(BluetoothClient client, CancellationToken ct)
    {
        using (client)
        try { await handler.HandleAsync(client.GetStream(), ct); }
        catch (Exception ex) { Console.Error.WriteLine($"Bluetooth client disconnected: {ex.Message}"); }
    }

    public void Dispose() => _listener?.Stop();
}
