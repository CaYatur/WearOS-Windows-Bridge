using System.Diagnostics;
using System.Security.Principal;

namespace Bridge.Windows;

internal static class FirewallManager
{
    private const string Prefix = "WearOS Windows Bridge";
    private static readonly (string Name, string Protocol, int Port)[] Rules =
    {
        ($"{Prefix} - Bridge TCP", "TCP", 38471),
        ($"{Prefix} - Discovery UDP", "UDP", 38472),
        ($"{Prefix} - Pairing TCP", "TCP", 38473),
    };

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool EnsureRules(bool interactive)
    {
        if (Rules.All(r => RuleExists(r.Name))) return true;
        if (!interactive) return false;
        return RepairRulesElevated();
    }

    public static bool RepairRulesElevated()
    {
        var commands = new List<string>();
        foreach (var r in Rules)
        {
            commands.Add($"netsh advfirewall firewall delete rule name=\"{r.Name}\"");
            commands.Add($"netsh advfirewall firewall add rule name=\"{r.Name}\" dir=in action=allow protocol={r.Protocol} localport={r.Port} profile=private enable=yes");
        }
        var script = string.Join(" & ", commands);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
            return p?.ExitCode == 0 && Rules.All(r => RuleExists(r.Name));
        }
        catch { return false; }
    }

    private static bool RuleExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
