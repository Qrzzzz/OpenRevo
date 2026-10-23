using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRevo.App;

// Keep this protocol in sync with src/OpenRevo.Background/main.cpp.
// The resident process has no CLR/WinUI or device connection.
internal static class DesktopLifetime
{
    internal const string UiProperty = "OpenRevo.Ui.v1";
    internal static readonly uint ShowMessage = RegisterWindowMessage("OpenRevo.Show.v1");
    internal static readonly uint ExitMessage = RegisterWindowMessage("OpenRevo.Exit.v1");
    internal static readonly uint TrayActionMessage = RegisterWindowMessage("OpenRevo.TrayAction.v1");
    internal static readonly uint TrayStateMessage = RegisterWindowMessage("OpenRevo.TrayState.v1");
    internal static string BackgroundPath => Path.Combine(AppContext.BaseDirectory, "OpenRevo.Background.exe");
    private const string BackgroundClass = "OpenRevo.Background.v1";
    private const uint StopBackgroundMessage = 0x8002;

    internal static bool BackgroundRunning => FindWindow(BackgroundClass, null) != 0;
    internal static void EnsureBackground()
    {
        var existing = FindWindow(BackgroundClass, null);
        if (existing != 0)
        {
            GetWindowThreadProcessId(existing, out var processId);
            using var previous = Process.GetProcessById((int)processId);
            if (string.Equals(previous.MainModule?.FileName, BackgroundPath, StringComparison.OrdinalIgnoreCase)) return;
            // A portable update must leave its own tray executable in charge of reopening.
            StopBackground();
            if (!previous.WaitForExit(3000)) throw new Win32Exception("旧版后台尚未退出，请从托盘退出后重试。");
        }
        if (!File.Exists(BackgroundPath)) throw new FileNotFoundException("缺少轻量后台程序，请完整解压发行包。", BackgroundPath);
        using var process = Process.Start(new ProcessStartInfo(BackgroundPath) { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) throw new Win32Exception("后台程序未能启动。");
        if (!process.WaitForInputIdle(3000) || process.HasExited || !BackgroundRunning)
            throw new Win32Exception("后台托盘未能就绪，请检查后重试。");
    }

    internal static void StopBackground()
    {
        var window = FindWindow(BackgroundClass, null);
        if (window != 0) PostMessage(window, StopBackgroundMessage, 0, 0);
    }

    internal static void RequestWindowClose(nint window) => PostMessage(window, 0x0010, 0, 0);

    internal static bool ShowExisting(OpenRevo.Core.TrayAction? action = null)
    {
        nint found = 0;
        EnumWindows((window, _) => { if (GetProp(window, UiProperty) == 0) return true; found = window; return false; }, 0);
        if (found == 0) return false;
        GetWindowThreadProcessId(found, out var processId);
        AllowSetForegroundWindow(processId);
        return action is null ? PostMessage(found, ShowMessage, 0, 0)
            : PostMessage(found, TrayActionMessage, (nuint)action.Value, 0);
    }

    internal delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    private delegate bool EnumProcedure(nint window, nint data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    internal static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProcedure callback, nint data);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    internal static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
