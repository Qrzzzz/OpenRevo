using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using System.Text.Json;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private readonly Button _fanOn = new() { Content = "开启一键强冷" };
    private readonly Button _fanAuto = new() { Content = "恢复自动调速" };
    private readonly TextBlock _fanState = Text("等待实时风扇状态", 14, true);
    private readonly TextBlock _fanMetrics = Text("等待风扇转速", 14);
    private readonly TextBlock _fanResult = Text("", 13);
    private static string FanJournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo", "fan-journal.json");

    private FrameworkElement FanControls()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text("风扇控制", 18, true));
        panel.Children.Add(Secondary("一键强冷调用原厂风扇加速；自动调速由原厂温控管理，无需 OpenRevo 持续运行。"));
        panel.Children.Add(_fanState); panel.Children.Add(_fanMetrics);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_fanOn); actions.Children.Add(_fanAuto); panel.Children.Add(actions);
        _fanOn.Click += async (_, _) => await OperateFanAsync(true);
        _fanAuto.Click += async (_, _) => await OperateFanAsync(false);
        AutomationProperties.SetLiveSetting(_fanResult, AutomationLiveSetting.Polite);
        _fanResult.IsTextSelectionEnabled = true;
        panel.Children.Add(_fanResult);
        panel.Children.Add(Secondary("切换性能模式前请先恢复自动调速。风扇操作会结束此前的模式撤销记录；关窗或断开不会自动改变风扇设置。曲线与手动转速暂不开放。"));
        return Card(panel);
    }

    private void RenderFanControls()
    {
        var pilot = _observer?.Pilot;
        _fanOn.IsEnabled = _connected && pilot?.CanControlFan == true && pilot.FanBoostEnabled == false;
        _fanAuto.IsEnabled = _connected && pilot?.CanControlFan == true && pilot.FanBoostEnabled == true;
        _fanState.Text = !_connected ? "已断开 · 连接后读取风扇状态" : pilot?.FanControlStatus ?? "等待实时风扇状态";
        var fan = Sample("System/FanInfo");
        _fanMetrics.Text = $"CPU {Number(fan, "CpuFanRpm", " RPM", 0, 20000)} · GPU {Number(fan, "GpuFanRpm", " RPM", 0, 20000)}\n" +
            $"占空比 CPU {Number(fan, "CpuFanDuty", "%", 0, 100)} · GPU {Number(fan, "GpuFanDuty", "%", 0, 100)} · " + (fan is null ? "尚无数据" : Age(fan));
        _fanResult.Visibility = string.IsNullOrWhiteSpace(_fanResult.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task OperateFanAsync(bool enabled)
    {
        var observer = _observer;
        if (observer is null || !_connected) return;
        _fanResult.Text = enabled ? "正在开启一键强冷…" : "正在恢复自动调速…";
        var operation = observer.Pilot.SetFanBoostAsync(enabled);
        Render();
        var result = await operation;
        if (_closed || observer != _observer) return;
        _fanResult.Text = result.Detail;
        Render();
        FrameworkElementAutomationPeer.CreatePeerForElement(_fanResult)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        var next = enabled ? _fanAuto : _fanOn;
        if (next.IsEnabled) next.Focus(FocusState.Programmatic);
        else _navigation.Focus(FocusState.Programmatic);
    }
}
