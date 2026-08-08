using Bridge.Protocol;
using Bridge.Windows;

using var instance = new SingleInstanceGuard();
if (!instance.IsOwner)
{
    MessageBox.Show("WearOS Windows Bridge is already running.", "WearOS Windows Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WearOSWindowsBridge", "pairing.key");
Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
byte[] key;
if (File.Exists(keyPath)) key = Convert.FromBase64String(await File.ReadAllTextAsync(keyPath));
else { key = BridgeCodec.NewPairingKey(); await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(key)); }

// Expose bridge/discovery/pairing only on Windows Private networks.
// Missing rules cause a standard UAC prompt on first launch.
FirewallManager.EnsureRules(interactive: true);

Log.Info($"WearOS Windows Bridge starting (protocol v{BridgeCodec.ProtocolVersion})");

// A crash on a background thread would otherwise vanish and just look like "it stopped working".
AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Warn($"unhandled exception: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn($"unobserved task exception: {e.Exception.Message}"); e.SetObserved(); };

var media = new WindowsMediaBridge();
await media.InitializeAsync();
using var features = new WindowsFeatures();
var handler = new BridgeConnectionHandler(media, features, key);
var status = new ConnectionStatus();
using var host = new BridgeHost(handler, status, key);
host.Start();
ApplicationConfiguration.Initialize();
Application.Run(new BridgeAppContext(key, status));
Log.Info("WearOS Windows Bridge stopped");
