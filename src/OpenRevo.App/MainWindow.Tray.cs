using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private bool _trayActionPending;

    // The tray only forwards a fixed intent. All writes stay in the existing pilot.
    internal async void RequestTrayAction(TrayAction action)
    {
        if (_closed || !Enum.IsDefined(action)) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        if (_trayActionPending)
        {
            ShowNotice("正在处理托盘操作", "请等待当前操作结束；新的请求未排队。", InfoBarSeverity.Informational);
            return;
        }
        _trayActionPending = true;
        try
        {
            NavigateFromTray(action is TrayAction.Overview ? "overview" :
                action is TrayAction.Settings or TrayAction.Diagnostics or TrayAction.Updates ? "settings" :
                action is TrayAction.Refresh ? "overview" : "pilot");
            if (action == TrayAction.Updates) { RevealUpdateCard(); _checkUpdates?.Invoke(); return; }
            if (!TrayActions.IsControl(action) && action is not (TrayAction.Refresh or TrayAction.Diagnostics)) return;
            ShowNotice("托盘快捷操作", "正在读取本机状态；本次请求只执行一次。", InfoBarSeverity.Informational);
            // Only wait during cold startup. Never reconnect, retry, or replay an intent.
            var startupGeneration = _generation;
            var deadline = Environment.TickCount64 + 15000;
            while (!_closed && (!_started || _sessionActive && !_connected ||
                _connected && (_observer?.Pilot.CurrentStatus is null || !_samples.ContainsKey("Customize/SupportInfo") || !_samples.ContainsKey("Tray/Status"))))
            {
                if (Environment.TickCount64 >= deadline || _started && _session is { IsCompleted: true }) break;
                await Task.Delay(100);
                if (_started && startupGeneration == 0) startupGeneration = _generation;
                if (_started && startupGeneration != _generation) break;
            }
            if (_closed) return;
            if (action == TrayAction.Diagnostics) { SaveAuditReport(); return; }
            var pilot = _observer?.Pilot;
            if (!_connected || pilot is null || startupGeneration != _generation)
            { ShowNotice("快捷操作未执行", "设备尚未连接。连接后可重新选择；本次请求不会自动重试。", InfoBarSeverity.Warning); return; }
            if (action == TrayAction.Refresh)
            { _observer!.RequestRefresh(); ShowNotice("已请求刷新", "正在重新读取设备数据。"); return; }
            if ((TrayActions.State(pilot, _connected) & TrayActions.Bit(action)) == 0)
            {
                var same = action is >= TrayAction.Office and <= TrayAction.Custom && pilot.CurrentMode == ((uint)action - 10).ToString()
                    || action == TrayAction.FanBoost && pilot.FanBoostEnabled == true || action == TrayAction.FanAuto && pilot.FanBoostEnabled == false;
                ShowNotice(same ? "已处于所选状态" : "快捷操作未执行", same ? "无需重复发送命令。" :
                    action is TrayAction.FanBoost or TrayAction.FanAuto ? pilot.FanControlStatus : pilot.ControlStatus,
                    same ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
                return;
            }
            _notice.IsOpen = false;
            if (action is >= TrayAction.Office and <= TrayAction.Custom) await OperateModeAsync((PilotMode)((uint)action - 10), false);
            else if (action is TrayAction.FanBoost or TrayAction.FanAuto) await OperateFanAsync(action == TrayAction.FanBoost);
            else if (action == TrayAction.Restore) await OperateModeAsync(default, true);
            else if (action == TrayAction.Keep) { ShowModeResult(pilot.KeepCurrent()); Render(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { if (!_closed) ShowNotice("快捷操作未完成", "请查看当前连接和设备状态，再重新操作。", InfoBarSeverity.Warning); }
        finally { _trayActionPending = false; }
    }

    private void NavigateFromTray(string page)
    {
        var items = _navigation.MenuItems.Concat(_navigation.FooterMenuItems).OfType<NavigationViewItem>();
        _navigation.SelectedItem = items.First(item => (string)item.Tag == page);
        ShowPage(page);
        if (_pages[page] is ScrollViewer scroll) scroll.ChangeView(null, 0, null, true);
    }
}
