namespace Bridge.Windows;

public sealed class ConnectionStatus
{
    private readonly object _gate = new();
    private int _lanClients;
    private int _bluetoothClients;
    private DateTimeOffset? _lastSeen;
    public event Action? Changed;

    public void SetConnected(string transport, bool connected)
    {
        lock (_gate)
        {
            if (transport == "LAN") _lanClients = Math.Max(0, _lanClients + (connected ? 1 : -1));
            if (transport == "Bluetooth") _bluetoothClients = Math.Max(0, _bluetoothClients + (connected ? 1 : -1));
            if (connected) _lastSeen = DateTimeOffset.Now;
        }
        Changed?.Invoke();
    }

    public (bool Connected, string Transport, DateTimeOffset? LastSeen) Snapshot()
    {
        lock (_gate)
        {
            var transport = _bluetoothClients > 0 ? "Bluetooth" : _lanClients > 0 ? "LAN" : "Disconnected";
            return (_bluetoothClients + _lanClients > 0, transport, _lastSeen);
        }
    }
}
