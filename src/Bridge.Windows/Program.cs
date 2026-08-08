using System.Net;
using System.Net.Sockets;
using System.Text;
using Bridge.Protocol;
using Bridge.Windows;

const int Port = 38471;
var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WearOSWindowsBridge", "pairing.key");
Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
byte[] key;
if (File.Exists(keyPath)) key = Convert.FromBase64String(await File.ReadAllTextAsync(keyPath));
else { key = BridgeCodec.NewPairingKey(); await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(key)); }

var media = new WindowsMediaBridge();
await media.InitializeAsync();
using var features = new WindowsFeatures();
var handler = new BridgeConnectionHandler(media, features, key);
var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
using var shutdown = new CancellationTokenSource();
using var bluetooth = new BluetoothServer(handler);
_ = bluetooth.RunAsync(shutdown.Token);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); listener.Stop(); };
Console.WriteLine($"WearOS Windows Bridge listening on LAN TCP {Port} and Bluetooth RFCOMM {BluetoothServer.ServiceId}");
Console.WriteLine("Pair this Android device once using the following local secret:");
Console.WriteLine(Convert.ToBase64String(key));
Console.WriteLine("Treat this value like a password. Regenerate it if it is ever exposed.");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        using (client)
        try { await handler.HandleAsync(client.GetStream()); }
        catch (Exception ex) { Console.Error.WriteLine($"Client disconnected: {ex.Message}"); }
    });
}
