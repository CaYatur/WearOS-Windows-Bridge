using NAudio.CoreAudioApi;

namespace Bridge.Windows;

/// <summary>
/// Volume, clipboard and machine stats. Each accessor swallows its own failures: an unplugged
/// audio endpoint or a clipboard another process is holding open must not tear down a connection.
/// </summary>
public sealed class WindowsFeatures : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    // Clipboard reads need an STA thread each time; polling one per frame is wasteful and
    // contends with whatever else is using the clipboard. A short cache is plenty for a watch face.
    private static readonly TimeSpan ClipboardCacheWindow = TimeSpan.FromSeconds(2);
    private readonly object _clipboardGate = new();
    private string? _clipboardCache;
    private DateTime _clipboardReadAt = DateTime.MinValue;

    private readonly object _cpuGate = new();
    private (long Idle, long Kernel, long User)? _lastCpuSample;
    private double _lastCpuPercent;

    public (double Volume, bool Muted) ReadAudio()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
        }
        catch (Exception ex) { Log.Warn($"audio read failed: {ex.Message}"); return (0d, false); }
    }

    public void SetAudio(double? volume, bool? muted)
    {
        if (volume is null && muted is null) return;
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (volume is { } v) device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(v, 0, 1);
            if (muted is { } m) device.AudioEndpointVolume.Mute = m;
        }
        catch (Exception ex) { Log.Warn($"audio write failed: {ex.Message}"); }
    }

    public string? ReadClipboardText()
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (_clipboardGate)
        {
            if (DateTime.UtcNow - _clipboardReadAt < ClipboardCacheWindow) return _clipboardCache;
            _clipboardReadAt = DateTime.UtcNow;
            string? result = null;
            // Another process can hold the clipboard open; that is an ordinary transient failure.
            if (!TryRunSta(() => { if (Clipboard.ContainsText()) result = Clipboard.GetText(); }, "clipboard read"))
                return _clipboardCache;
            _clipboardCache = result is { Length: > 32_768 } ? result[..32_768] : result;
            return _clipboardCache;
        }
    }

    public void SetClipboardText(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (text.Length > 32_768) { Log.Warn("clipboard write rejected: text too long"); return; }
        if (TryRunSta(() => Clipboard.SetText(text), "clipboard write"))
            lock (_clipboardGate) { _clipboardCache = text; _clipboardReadAt = DateTime.UtcNow; }
    }

    public static double ReadMemoryPercent()
    {
        try
        {
            var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
            if (info.TotalPhysicalMemory == 0) return 0;
            return Math.Clamp(100.0 * (info.TotalPhysicalMemory - info.AvailablePhysicalMemory) / info.TotalPhysicalMemory, 0, 100);
        }
        catch (Exception ex) { Log.Warn($"memory read failed: {ex.Message}"); return 0; }
    }

    /// <summary>
    /// Whole-machine CPU load. This used to report the bridge's own process time, which is near
    /// zero and made the watch's PC-status readout meaningless.
    /// </summary>
    public double ReadCpuPercent()
    {
        lock (_cpuGate)
        {
            try
            {
                if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return _lastCpuPercent;
                var sample = (Idle: ToLong(idleFt), Kernel: ToLong(kernelFt), User: ToLong(userFt));
                if (_lastCpuSample is not { } previous) { _lastCpuSample = sample; return _lastCpuPercent; }

                // Kernel time already includes idle time, so total busy = (kernel + user) - idle.
                var idleDelta = sample.Idle - previous.Idle;
                var totalDelta = sample.Kernel - previous.Kernel + (sample.User - previous.User);
                _lastCpuSample = sample;
                if (totalDelta <= 0) return _lastCpuPercent;
                _lastCpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
                return _lastCpuPercent;
            }
            catch (Exception ex) { Log.Warn($"cpu read failed: {ex.Message}"); return _lastCpuPercent; }
        }
    }

    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    /// <summary>Runs an action on an STA thread. Returns false instead of throwing on failure.</summary>
    private static bool TryRunSta(Action action, string what)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(3))) { Log.Warn($"{what} timed out"); return false; }
        if (error is not null) { Log.Warn($"{what} failed: {error.Message}"); return false; }
        return true;
    }

    public void Dispose() => _enumerator.Dispose();
}
