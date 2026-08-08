using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Bridge.Protocol;

namespace Bridge.Windows;

public sealed class BridgeAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly byte[] _key;
    private readonly ConnectionStatus _status;
    private readonly ToolStripMenuItem _connectionItem;
    public BridgeAppContext(byte[] key, ConnectionStatus status)
    {
        _key = key;
        _status = status;
        var menu = new ContextMenuStrip();
        _connectionItem = new ToolStripMenuItem("Connection: Disconnected") { Enabled=false };
        menu.Items.Add(_connectionItem);
        menu.Items.Add("Pairing info", null, (_,_) => ShowPairing());
        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick=true, Checked=StartupManager.IsEnabled() };
        startup.CheckedChanged += (_,_) => { try { StartupManager.SetEnabled(startup.Checked); } catch(Exception ex) { MessageBox.Show(ex.Message,"Startup setting",MessageBoxButtons.OK,MessageBoxIcon.Error); startup.Checked=StartupManager.IsEnabled(); } };
        menu.Items.Add(startup);
        menu.Items.Add("Repair firewall", null, (_,_) => RepairFirewall());
        menu.Items.Add("Open log", null, (_,_) => OpenLog());
        menu.Items.Add("Open GitHub", null, (_,_) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/CaYatur/WearOS-Windows-Bridge") { UseShellExecute=true }));
        menu.Items.Add("Exit", null, (_,_) => ExitThread());
        _tray = new NotifyIcon { Text="WearOS Windows Bridge - Disconnected", Icon=SystemIcons.Application, Visible=true, ContextMenuStrip=menu };
        _tray.DoubleClick += (_,_) => ShowPairing();
        _status.Changed += UpdateConnection;
        UpdateConnection();
    }

    private void UpdateConnection()
    {
        if (_tray.ContextMenuStrip?.InvokeRequired == true) { _tray.ContextMenuStrip.BeginInvoke(UpdateConnection); return; }
        var s=_status.Snapshot();
        _connectionItem.Text=s.Connected ? $"Connection: {s.Transport}" : $"Connection: Disconnected{(s.LastSeen is null ? "" : $" (last {s.LastSeen:HH:mm:ss})")}";
        _tray.Text=("WearOS Windows Bridge - "+(s.Connected?s.Transport:"Disconnected"))[..Math.Min(63,("WearOS Windows Bridge - "+(s.Connected?s.Transport:"Disconnected")).Length)];
    }

    private void RepairFirewall()
    {
        var ok = FirewallManager.RepairRulesElevated();
        MessageBox.Show(ok ? "Private-network firewall rules are ready." : "Firewall rules could not be created. Check the UAC prompt and Windows Firewall service.", "WearOS Windows Bridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) { Log.Info("log opened from tray"); }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {Log.FilePath}\n\n{ex.Message}", "WearOS Windows Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
