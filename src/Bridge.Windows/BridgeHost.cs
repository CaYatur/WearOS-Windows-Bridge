using System.Net;
using System.Net.Sockets;

namespace Bridge.Windows;

public sealed class BridgeHost : IDisposable
{
    private const int Port=38471;
    private readonly BridgeConnectionHandler _handler;
    private readonly CancellationTokenSource _shutdown=new();
    private readonly TcpListener _tcp=new(IPAddress.Any,Port);
    private readonly BluetoothServer _bluetooth;
    private readonly ConnectionStatus _status;
    private readonly LanDiscoveryServer _discovery=new();
    public BridgeHost(BridgeConnectionHandler handler, ConnectionStatus status){_handler=handler;_status=status;_bluetooth=new(handler);}

    public void Start(){_tcp.Start();_discovery.Start(); _=RunTcpAsync(); _=RunBluetoothAsync();}
    private async Task RunTcpAsync(){while(!_shutdown.IsCancellationRequested) try { var client=await _tcp.AcceptTcpClientAsync(_shutdown.Token); _=HandleTcpAsync(client); } catch(OperationCanceledException){break;} catch(Exception ex){Console.Error.WriteLine($"LAN accept failed: {ex.Message}");}}
    private async Task HandleTcpAsync(TcpClient client){using(client) try{_status.SetConnected("LAN",true);await _handler.HandleAsync(client.GetStream(),_shutdown.Token);}catch(Exception ex){Console.Error.WriteLine($"LAN client disconnected: {ex.Message}");}finally{_status.SetConnected("LAN",false);}}
    private async Task RunBluetoothAsync(){try{await _bluetooth.RunAsync(_shutdown.Token);}catch(OperationCanceledException){}catch(Exception ex){Console.Error.WriteLine($"Bluetooth server unavailable: {ex.Message}");}}
    public void Dispose(){_shutdown.Cancel();_tcp.Stop();_discovery.Dispose();_bluetooth.Dispose();_shutdown.Dispose();}
}
