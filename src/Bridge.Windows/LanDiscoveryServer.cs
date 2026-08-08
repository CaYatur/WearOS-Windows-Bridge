using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bridge.Windows;

public sealed class LanDiscoveryServer : IDisposable
{
    public const int DiscoveryPort=38472;
    private readonly CancellationTokenSource _cts=new();
    private UdpClient? _udp;
    public void Start()=>_=RunAsync();
    private async Task RunAsync()
    {
        _udp=new UdpClient(DiscoveryPort);
        while(!_cts.IsCancellationRequested) try
        {
            var r=await _udp.ReceiveAsync(_cts.Token);
            if(Encoding.UTF8.GetString(r.Buffer)=="WEARBRIDGE_DISCOVER_V1")
            {
                var b=Encoding.UTF8.GetBytes("WEARBRIDGE_HERE_V1|38471");
                await _udp.SendAsync(b,r.RemoteEndPoint,_cts.Token);
            }
        } catch(OperationCanceledException){break;} catch(Exception ex){Console.Error.WriteLine($"Discovery: {ex.Message}");}
    }
    public void Dispose(){_cts.Cancel();_udp?.Dispose();_cts.Dispose();}
}
