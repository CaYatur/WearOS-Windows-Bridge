using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Bridge.Windows;

public sealed class WindowsFeatures : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastCpuAt = DateTime.UtcNow;

    public (double Volume, bool Muted) ReadAudio()
    {
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
    }

    public void SetAudio(double? volume, bool? muted)
    {
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (volume is { } v) device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(v, 0, 1);
        if (muted is { } m) device.AudioEndpointVolume.Mute = m;
    }

    public string? ReadClipboardText()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? result = null;
        RunSta(() => { if (Clipboard.ContainsText()) result = Clipboard.GetText(); });
        return result is { Length: > 32_768 } ? result[..32_768] : result;
    }

    public void SetClipboardText(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (text.Length > 32_768) throw new ArgumentOutOfRangeException(nameof(text));
        RunSta(() => Clipboard.SetText(text));
    }

    public static double ReadMemoryPercent()
    {
        var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
        if (info.TotalPhysicalMemory == 0) return 0;
        return Math.Clamp(100.0 * (info.TotalPhysicalMemory - info.AvailablePhysicalMemory) / info.TotalPhysicalMemory, 0, 100);
    }

    public double ReadCpuPercent()
    {
        var now = DateTime.UtcNow;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = now - _lastCpuAt;
        var used = cpu - _lastCpu;
        _lastCpuAt = now; _lastCpu = cpu;
        if (wall.TotalMilliseconds <= 0) return 0;
        return Math.Clamp(100.0 * used.TotalMilliseconds / (wall.TotalMilliseconds * Environment.ProcessorCount), 0, 100);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw error;
    }

    public void Dispose() => _enumerator.Dispose();
}
