using System.Net;
using System.Net.Sockets;

namespace Bridge.Windows;

public sealed class BridgeHost : IDisposable
{
    public const int Port = 38471;
    private readonly BridgeConnectionHandler _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TcpListener _tcp = new(IPAddress.Any, Port);
    private readonly BluetoothServer _bluetooth;
    private readonly ConnectionStatus _status;
    private readonly LanDiscoveryServer _discovery = new();
    private readonly AutoPairingServer _pairing;

    public BridgeHost(BridgeConnectionHandler handler, ConnectionStatus status, byte[] key)
    {
        _handler = handler;
        _status = status;
        _bluetooth = new BluetoothServer(handler, status);
        _pairing = new AutoPairingServer(key);
    }

    public void Start()
    {
        try { _tcp.Start(); Log.Info($"LAN listener on port {Port}"); }
        catch (Exception ex) { Log.Warn($"LAN listener failed to start on port {Port}: {ex.Message}"); }
        _discovery.Start();
        _pairing.Start();
        _ = RunTcpAsync();
        _ = RunBluetoothAsync();
    }

    private async Task RunTcpAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var client = await _tcp.AcceptTcpClientAsync(_shutdown.Token);
                _ = HandleTcpAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"LAN accept failed: {ex.Message}");
                try { await Task.Delay(1000, _shutdown.Token); } catch { break; }
            }
        }
    }

    private async Task HandleTcpAsync(TcpClient client)
    {
        using (client)
        {
            // A dropped Wi-Fi link leaves a half-open socket that never reports EOF. Keepalives make
            // the OS notice, so the tray stops claiming a connection that is already gone.
            try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
            client.NoDelay = true;
            var peer = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Log.Info($"LAN client connected: {peer}");
            _status.SetConnected("LAN", true);
            try { await _handler.HandleAsync(client.GetStream(), "LAN", _shutdown.Token); }
            catch (Exception ex) { Log.Info($"LAN client {peer} disconnected: {ex.Message}"); }
            finally { _status.SetConnected("LAN", false); Log.Info($"LAN client disconnected: {peer}"); }
        }
    }

    private async Task RunBluetoothAsync()
    {
        try { await _bluetooth.RunAsync(_shutdown.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Bluetooth server unavailable: {ex.Message}"); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _tcp.Stop(); } catch { }
        _discovery.Dispose();
        _pairing.Dispose();
        _bluetooth.Dispose();
        _shutdown.Dispose();
    }
}
