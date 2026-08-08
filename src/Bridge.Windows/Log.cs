namespace Bridge.Windows;

/// <summary>
/// Small append-only log next to the pairing key. The tray app has no console, so without this a
/// rejected frame or a failing subsystem read is invisible and the link just looks "unstable".
/// Repeated identical messages are collapsed so a per-poll failure cannot fill the disk.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WearOSWindowsBridge", "bridge.log");

    private static string? _lastMessage;
    private static int _repeats;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (message == _lastMessage)
                {
                    // Log the 1st, 10th, 100th... occurrence so a stuck state stays visible without spam.
                    _repeats++;
                    if (_repeats % 10 != 0 || _repeats > 1000) return;
                    message = $"{message} (repeated {_repeats}x)";
                }
                else { _lastMessage = message; _repeats = 0; }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
                Console.Error.WriteLine(line);
                var directory = System.IO.Path.GetDirectoryName(Path)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllLines(Path, [line]);
            }
        }
        catch { /* logging must never break the bridge */ }
    }

    public static string FilePath => Path;
}
