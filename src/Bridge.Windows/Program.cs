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
var enabled = BridgeFeature.Media | BridgeFeature.PcStatus;
var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"WearOS Windows Bridge listening on LAN TCP {Port}");
Console.WriteLine("Pairing key is stored in LocalAppData and is intentionally not printed.");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleAsync(client);
}

async Task HandleAsync(TcpClient client)
{
    using var ownedClient = client;
    using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true };
    while (client.Connected)
    {
        var line = await reader.ReadLineAsync(); if (line is null) break;
        SignedEnvelope? request;
        try { request = BridgeCodec.Deserialize(line); } catch { continue; }
        if (request is null || !BridgeCodec.Verify(request, key, TimeSpan.FromMinutes(2))) continue;
        if (request.Type == BridgeMessageType.Command && request.Payload.Command is { } command) await media.ExecuteAsync(command);
        var state = await media.ReadAsync();
        var response = BridgeCodec.Sign(BridgeMessageType.State, new BridgePayload(enabled, state), key);
        await writer.WriteLineAsync(BridgeCodec.Serialize(response));
    }
}
