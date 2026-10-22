using Microsoft.Win32;
using System.Runtime.Versioning;

namespace OpenRevo.App;

internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "OpenRevo";
    internal static bool Matches(string registered, string executable) =>
        string.Equals(registered, $"\"{executable}\" --autostart", StringComparison.OrdinalIgnoreCase);
    internal static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("需要完整的后台程序路径。", nameof(executable));
        var command = $"\"{executable}\" --autostart";
        if (command.Length > 260) throw new ArgumentException("程序路径过长，请将 OpenRevo 移到较短的路径后启用开机启动。", nameof(executable));
        return command;
    }

    [SupportedOSPlatform("windows")]
    internal static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    [SupportedOSPlatform("windows")]
    internal static void Set(bool enabled, string executable)
    {
        // Do not create a key or a default entry merely by opening Settings.
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var command = Command(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少轻量后台程序，请完整解压发行包。", executable);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
