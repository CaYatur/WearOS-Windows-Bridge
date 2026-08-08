using Microsoft.Win32;

namespace Bridge.Windows;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WearOSWindowsBridge";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
