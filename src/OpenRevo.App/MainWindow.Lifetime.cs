using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private bool _exitRequested;
    private nint _nativeWindow;
    private DesktopLifetime.SubclassProcedure? _lifetimeProcedure;
    private readonly TextBlock _startupHint = Text("", 13);

    private void AttachDesktopLifetime()
    {
        _nativeWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lifetimeProcedure = LifetimeMessage;
        if (!DesktopLifetime.SetWindowSubclass(_nativeWindow, _lifetimeProcedure, 1, 0)
            || !DesktopLifetime.SetProp(_nativeWindow, DesktopLifetime.UiProperty, 1))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        if (_preferences.StayInTray)
        {
            try { DesktopLifetime.EnsureBackground(); }
            catch (Exception ex) when (LifetimeFailure(ex))
            { ShowNotice("后台暂不可用", "关闭窗口后将退出。请确认完整解压了发行包，再重新开启后台驻留。", InfoBarSeverity.Warning); }
        }
    }

    private nint LifetimeMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == DesktopLifetime.TrayStateMessage)
            return _closed || _trayActionPending ? 0 : (nint)OpenRevo.Core.TrayActions.State(_observer?.Pilot, _connected);
        if (message == DesktopLifetime.TrayActionMessage)
        {
            if (wParam <= uint.MaxValue && Enum.IsDefined((OpenRevo.Core.TrayAction)(uint)wParam))
                RequestTrayAction((OpenRevo.Core.TrayAction)(uint)wParam);
            return 0;
        }
        if (message == DesktopLifetime.ShowMessage)
        {
            if (!_closed)
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore();
                Activate();
            }
            return 0;
        }
        if (message == DesktopLifetime.ExitMessage)
        { RequestExit(); return 0; }
        // Windows logoff/shutdown must never create another resident process.
        if (message == 0x0011) _exitRequested = true;
        if (message == 0x0016) _exitRequested = wParam != 0;
        return DesktopLifetime.DefSubclassProc(window, message, wParam, lParam);
    }

    private UIElement LifetimeSettings()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text("启动与后台", 18, true));
        string? startup = null;
        try { startup = StartupRegistration.Read(); }
        catch (Exception ex) when (LifetimeFailure(ex)) { _startupHint.Text = "无法读取开机启动设置，请检查当前用户权限。"; }
        var autoStart = new ToggleSwitch { Header = "开机自启动", OnContent = "开启", OffContent = "关闭", IsOn = startup is not null };
        panel.Children.Add(autoStart);
        panel.Children.Add(Secondary("默认关闭。经常使用时可按需开启：登录 Windows 后仅驻留托盘，不弹出主窗口。关闭自启动不影响原厂后台维持当前硬件模式。"));
        if (startup is not null && !StartupRegistration.Matches(startup, DesktopLifetime.BackgroundPath))
            _startupHint.Text = "开机启动仍指向其他文件夹。关闭再开启此开关，可改用当前版本。";
        panel.Children.Add(_startupHint);
        var updating = false;
        autoStart.Toggled += (_, _) =>
        {
            if (updating) return;
            try
            {
                StartupRegistration.Set(autoStart.IsOn, DesktopLifetime.BackgroundPath);
                _startupHint.Text = autoStart.IsOn
                    ? "已登记当前版本。若在 Windows“启动应用”中禁用，还需在那里重新启用。"
                    : "已关闭开机自启动。";
            }
            catch (Exception ex) when (LifetimeFailure(ex))
            {
                updating = true; autoStart.IsOn = !autoStart.IsOn; updating = false;
                _startupHint.Text = "未能更改开机启动：" + ex.Message;
            }
        };
        var stay = new ToggleSwitch { Header = "关闭窗口后驻留托盘", OnContent = "开启", OffContent = "关闭", IsOn = _preferences.StayInTray };
        stay.Toggled += (_, _) =>
        {
            try
            {
                if (stay.IsOn) DesktopLifetime.EnsureBackground();
                else DesktopLifetime.StopBackground();
                _preferences = _preferences with { StayInTray = stay.IsOn }; SavePreferences();
            }
            catch (Exception ex) when (LifetimeFailure(ex))
            { stay.IsOn = false; ShowNotice("后台未能开启", ex.Message, InfoBarSeverity.Warning); }
        };
        panel.Children.Add(stay);
        panel.Children.Add(Secondary("关闭后释放界面与监控连接，只保留轻量托盘。当前硬件模式继续有效；再次打开会重新读取设备，不自动切换模式。"));
        var exit = new Button { Content = "退出 OpenRevo" };
        exit.Click += (_, _) => RequestExit();
        panel.Children.Add(exit);
        return Card(panel);
    }

    private void RequestExit()
    {
        _exitRequested = true;
        // Window.Close bypasses AppWindow.Closing; WM_CLOSE runs the awaited session cleanup.
        DesktopLifetime.RequestWindowClose(_nativeWindow);
    }

    private static bool LifetimeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or Win32Exception or InvalidOperationException or ArgumentException;
}
