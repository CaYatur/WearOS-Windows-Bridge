using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Bridge.Protocol;

namespace Bridge.Windows;

public sealed class BridgeAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly byte[] _key;
    public BridgeAppContext(byte[] key)
    {
        _key = key;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Pairing info", null, (_,_) => ShowPairing());
        menu.Items.Add("Open GitHub", null, (_,_) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/CaYatur/WearOS-Windows-Bridge") { UseShellExecute=true }));
        menu.Items.Add("Exit", null, (_,_) => ExitThread());
        _tray = new NotifyIcon { Text="WearOS Windows Bridge", Icon=SystemIcons.Application, Visible=true, ContextMenuStrip=menu };
        _tray.DoubleClick += (_,_) => ShowPairing();
    }

    private void ShowPairing()
    {
        var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a=>a.Address.ToString()).Distinct();
        MessageBox.Show($"PC IP: {string.Join(", ",ips)}\nPort: 38471\nBluetooth service: {BluetoothServer.ServiceId}\n\nPairing key:\n{Convert.ToBase64String(_key)}\n\nKeep this key private.", "WearOS Windows Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore() { _tray.Visible=false; _tray.Dispose(); base.ExitThreadCore(); }
}
