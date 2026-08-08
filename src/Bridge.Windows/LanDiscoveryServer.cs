using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bridge.Windows;

/// <summary>
/// Answers the client's "where is the PC" broadcast so the watch never needs a typed-in IP.
/// The socket is created inside the retry loop: a port conflict used to fault the task before the
/// loop even started, leaving discovery permanently dead with nothing logged.
/// </summary>
public sealed class LanDiscoveryServer : IDisposable
{
    public const int DiscoveryPort = 38472;
    private const string Probe = "WEARBRIDGE_DISCOVER_V1";
    private const string Reply = "WEARBRIDGE_HERE_V1|38471";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _udp = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
                Log.Info($"LAN discovery listening on UDP {DiscoveryPort}");
                await ReceiveLoopAsync(_udp, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"LAN discovery stopped: {ex.Message}. Retrying in {RetryDelay.TotalSeconds:0}s"); }
            finally { _udp?.Dispose(); _udp = null; }

            if (_cts.IsCancellationRequested) break;
            try { await Task.Delay(RetryDelay, _cts.Token); } catch { break; }
        }
    }

    private static async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        var reply = Encoding.UTF8.GetBytes(Reply);
        while (!ct.IsCancellationRequested)
        {
            var received = await udp.ReceiveAsync(ct);
            if (Encoding.UTF8.GetString(received.Buffer).TrimEnd('\0', '\n', '\r') != Probe) continue;
            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, ct);
                Log.Info($"LAN discovery answered {received.RemoteEndPoint}");
            }
            // A single unreachable requester must not take down the loop.
            catch (SocketException ex) { Log.Warn($"LAN discovery reply to {received.RemoteEndPoint} failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
