namespace Bridge.Windows;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsOwner { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, "Local\\WearOSWindowsBridge.SingleInstance", out var createdNew);
        IsOwner = createdNew;
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
