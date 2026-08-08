using Bridge.Protocol;
using Bridge.Windows;
var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WearOSWindowsBridge", "pairing.key");
Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
byte[] key;
if (File.Exists(keyPath)) key = Convert.FromBase64String(await File.ReadAllTextAsync(keyPath));
else { key = BridgeCodec.NewPairingKey(); await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(key)); }

var media = new WindowsMediaBridge();
await media.InitializeAsync();
using var features = new WindowsFeatures();
var handler = new BridgeConnectionHandler(media, features, key);
using var host = new BridgeHost(handler);
host.Start();
ApplicationConfiguration.Initialize();
Application.Run(new BridgeAppContext(key));
