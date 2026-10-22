using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private readonly ProgressRing _connectionProgress = new() { Width = 18, Height = 18 };
    private readonly Grid _modeChoices = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Dictionary<PilotMode, TextBlock> _choiceTitles = [];
    private readonly TextBlock _currentModeTitle = Text("等待模式数据", 28, true);
    private readonly TextBlock _currentModeDetail = Secondary("连接后显示当前模式和供电状态");
    private readonly StackPanel _modeCompletion = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly InfoBar _modeFeedback = new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly ProgressBar _modeProgress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly TextBlock _deviceSummary = Text("正在检测本机", 14);
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success };
    private ModePilotResult? _lastModeResult;

    private static TextBlock Secondary(string text) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryBodyStyle"] };

    private static Expander Details(string title, UIElement content) => new()
    { Header = title, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private FrameworkElement ConnectionChrome()
    {
        var container = new StackPanel { Spacing = 8, Margin = new Thickness(24, 0, 24, 8) };
        var bar = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(_connectionProgress);
        var labels = new StackPanel { Spacing = 2 }; labels.Children.Add(_connection); labels.Children.Add(_device);
        status.Children.Add(labels); bar.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var export = new Button { Content = "导出诊断" };
        export.Click += async (_, _) => await ExportAsync();
        ToolTipService.SetToolTip(export, "保存当前诊断数据 (Ctrl+S)");
        actions.Children.Add(_refresh); actions.Children.Add(export); actions.Children.Add(_connect);
        Grid.SetColumn(actions, 1); bar.Children.Add(actions);
        bar.SizeChanged += (_, _) =>
        {
            var compact = bar.ActualWidth < 800;
            Grid.SetRow(actions, compact ? 1 : 0); Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetColumnSpan(status, compact ? 2 : 1); Grid.SetColumnSpan(actions, compact ? 2 : 1);
        };
        var diagnostics = new HyperlinkButton { Content = "查看诊断" };
        diagnostics.Click += (_, _) => _navigation.SelectedItem = _navigation.FooterMenuItems[1];
        _error.ActionButton = diagnostics;
        container.Children.Add(bar); container.Children.Add(_error); container.Children.Add(_notice);
        return container;
    }

    private void ConfigureModeChoices()
    {
        var choices = new[]
        {
            (_officeMode, PilotMode.Office, "办公模式", "安静办公，减少功耗"),
            (_balancedMode, PilotMode.Balanced, "均衡模式", "兼顾日常使用与游戏"),
            (_turboMode, PilotMode.Turbo, "狂暴模式", "优先释放性能"),
            (_customMode, PilotMode.Custom, "自定义模式", "使用原厂已保存的配置")
        };
        foreach (var (button, mode, title, description) in choices)
        {
            var content = new StackPanel { Spacing = 4 };
            var label = Text(title, 15, true); _choiceTitles[mode] = label;
            content.Children.Add(label); content.Children.Add(Secondary(description));
            button.Content = content; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.MinHeight = 80;
            button.Padding = new Thickness(16, 12, 16, 12);
            AutomationProperties.SetName(button, "切换到" + title);
            AutomationProperties.SetHelpText(button, description);
            _modeChoices.Children.Add(button);
        }
        _modeChoices.SizeChanged += (_, _) =>
        {
            var columns = _modeChoices.ActualWidth >= 760 ? 4 : 2;
            if (_modeChoices.ColumnDefinitions.Count == columns) return;
            _modeChoices.ColumnDefinitions.Clear(); _modeChoices.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) _modeChoices.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 4 / columns; i++) _modeChoices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < 4; i++) { var button = (FrameworkElement)_modeChoices.Children[i]; Grid.SetColumn(button, i % columns); Grid.SetRow(button, i / columns); }
        };
    }

    private void LayoutPages()
    {
        var top = _navigation.DisplayMode == NavigationViewDisplayMode.Minimal ? 56 : 20;
        foreach (var page in _pages.Values)
        {
            var content = page is ScrollViewer scroll ? scroll.Content as FrameworkElement : page as FrameworkElement;
            if (content is not null && content.Margin.Top != top) content.Margin = new Thickness(24, top, 24, 28);
        }
    }

    private void RenderPresentation()
    {
        LayoutPages();
        var running = _session is { IsCompleted: false };
        var phase = _observer?.Phase ?? MonitorPhase.Idle;
        _connectionProgress.IsActive = running && phase != MonitorPhase.Monitoring;
        _connectionProgress.Visibility = _connectionProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _connection.Text = !running ? (_samples.Count == 0 ? "尚未连接" : "已断开 · 显示上次数据") : phase switch
        {
            MonitorPhase.Connecting => "正在连接", MonitorPhase.Authenticating => "正在验证连接",
            MonitorPhase.Subscribing or MonitorPhase.WaitingCapabilities => "正在获取设备状态",
            MonitorPhase.Monitoring => "已连接 · 正在监测", _ => "正在检测设备"
        };
        _connect.Content = running ? "断开连接" : "连接设备";
        _connect.Style = running ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        _deviceSummary.Text = _inspection is null ? "等待检测结果" :
            _inspection.DeviceMatch == "matched" && _inspection.FileMatch == "matched" ? "设备与后台版本已匹配" : "设备或后台版本尚未匹配，请查看详情";

        var pilot = _observer?.Pilot; var journal = pilot?.Journal;
        var mode = Sample("Fan/Status"); var modeName = DeviceAudit.ModeName(mode?.Text("OperatingMode") ?? pilot?.CurrentMode);
        _currentModeTitle.Text = mode is null ? "等待模式数据" : pilot?.CurrentMode is null ? "暂时无法识别" : modeName;
        _currentModeDetail.Text = mode is null ? "连接后会自动读取当前模式" :
            (mode.Text("IsAC") is "True" or "true" ? "已接通电源" : "使用电池或等待供电确认") + " · " + Age(mode);
        foreach (var choice in _choiceTitles)
        {
            var current = pilot?.CurrentMode == ((int)choice.Key).ToString();
            choice.Value.Text = DeviceAudit.ModeName(((int)choice.Key).ToString()) + (current ? " · 当前" : "");
        }
        foreach (var (button, choice) in new[] { (_officeMode, PilotMode.Office), (_balancedMode, PilotMode.Balanced), (_turboMode, PilotMode.Turbo), (_customMode, PilotMode.Custom) })
            AutomationProperties.SetName(button, (pilot?.CurrentMode == ((int)choice).ToString() ? "当前模式：" : "切换到") + DeviceAudit.ModeName(((int)choice).ToString()));
        var busy = pilot?.Busy == true;
        _modeProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _modeCompletion.Visibility = journal?.Baseline is not null && journal.Phase is
            PilotPhase.Applied or PilotPhase.Sending or PilotPhase.AwaitingReadback or PilotPhase.Restoring or PilotPhase.Uncertain or PilotPhase.Interrupted
            ? Visibility.Visible : Visibility.Collapsed;
        _modeFeedback.Severity = InfoBarSeverity.Informational;
        if (busy) { _modeFeedback.Title = "正在核对切换结果"; _modeFeedback.Message = "请稍候，完成后会显示结果。"; }
        else if (journal?.Phase is PilotPhase.Uncertain or PilotPhase.Interrupted)
        {
            _modeFeedback.Severity = InfoBarSeverity.Warning; _modeFeedback.Title = "请先确认当前模式";
            _modeFeedback.Message = "本次操作已停止。请在原厂控制中心核对当前模式，再重新连接设备。";
        }
        else if (_lastModeResult is { Succeeded: false })
        {
            _modeFeedback.Severity = InfoBarSeverity.Warning; _modeFeedback.Title = "未能完成操作";
            _modeFeedback.Message = FriendlyControlReason(_lastModeResult.Detail);
        }
        else if (!_connected || pilot?.CanSwitch != true)
        {
            _modeFeedback.Title = "暂时无法切换模式";
            _modeFeedback.Message = !_connected ? "请先连接设备。连接失败时，可在设置与诊断中查看原因。" : FriendlyControlReason(pilot?.ControlStatus ?? "正在等待设备状态，请稍候。");
        }
        else if (journal?.Phase is PilotPhase.Applied or PilotPhase.Restored or PilotPhase.Kept)
        {
            _modeFeedback.Severity = InfoBarSeverity.Success;
            _modeFeedback.Title = journal.Phase switch
            { PilotPhase.Applied => "已切换到" + modeName, PilotPhase.Restored => "已撤销上次切换", _ => "已保留" + modeName };
            _modeFeedback.Message = journal.Phase == PilotPhase.Applied ? "可继续选择其他模式，或保留当前模式、撤销上次切换。" : "下次切换会重新记录切换前的配置。";
        }
        else
        {
            var available = _connected && pilot?.CanSwitch == true;
            _modeFeedback.Title = available ? "选择要使用的模式" : "暂时无法切换模式";
            _modeFeedback.Message = available ? "切换后会自动核对结果，自定义模式使用原厂已保存的配置。" :
                !_connected ? "请先连接设备。连接失败时，可在设置与诊断中查看原因。" : FriendlyControlReason(pilot?.ControlStatus ?? "等待设备状态");
        }
        foreach (var text in new[] { _modeResult, _previousPilot, _auditHint })
            text.Visibility = string.IsNullOrWhiteSpace(text.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FriendlyControlReason(string detail) => detail
        .Replace("当前 FanSettingsSupport 未声明模式控制支持；监测仍可继续。", "厂商服务暂未开放模式切换，仍可查看硬件状态。")
        .Replace("关键配置与已审计结果不一致，请核对原厂配置并保存审计材料。", "当前配置尚未验证。请在原厂控制中心核对配置，或到设置与诊断保存报告。")
        .Replace("实时能力未收到或已过期，请刷新后等待新响应。", "正在等待可用功能信息，请刷新数据后稍候。")
        .Replace("模式配置已过期，请刷新并等待实时响应。", "当前模式数据尚未更新，请刷新数据后稍候。")
        .Replace("关键配置缺失或无法识别模式，请查看具体差异并保存审计材料。", "当前模式信息不完整，请刷新数据；若仍无法识别，可在设置与诊断保存报告。")
        .Replace("监控授权已失效。", "设备连接已失效，请重新连接。")
        .Replace("两种状态的模式尚未一致，暂不允许发送。", "原厂回报的模式尚未一致，请刷新数据后稍候。");

    private static string InterfaceCopy(string text) => text.Replace("连接与外观", "设置与诊断")
        .Replace("保存审计材料", "保存诊断报告").Replace("原厂界面", "原厂控制中心")
        .Replace("尚未通过审计", "尚未验证").Replace("已审计", "已验证").Replace("重新审计", "重新验证");

    private static string DeviceCaption(DeviceIdentity device) => (device.Manufacturer == "MECHREVO" ? "机械革命" : device.Manufacturer) + " · " + device.Model;

    private static string MatchLabel(string value) => value switch
    { "matched" => "匹配", "mismatch" or "size mismatch" or "hash mismatch" => "不匹配", "pending" => "检测中", _ => "未检查" };

    private static string DiagnosticTitle(DiagnosticCode code) => code switch
    {
        DiagnosticCode.UnsupportedDevice => "此机型或 BIOS 尚未验证", DiagnosticCode.ServiceVersionMismatch => "原厂后台版本不匹配",
        DiagnosticCode.InstallationNotFound => "未找到原厂后台", DiagnosticCode.SlotOccupied => "设备连接已被占用",
        DiagnosticCode.SystemSuspended => "系统休眠，连接已结束", DiagnosticCode.ExportFailed => "无法导出诊断",
        DiagnosticCode.CapabilityRevoked => "设备状态已改变，连接已停止", DiagnosticCode.CapabilityExpired => "未收到最新设备状态",
        _ => "设备连接已中断"
    };

    private static string FeatureDescription(string field) => field switch
    {
        "SystemMonitorSupport" => "查看处理器、显卡、风扇和电池状态。",
        "FanSettingsSupport" => "切换四种性能模式；自定义使用原厂已保存的配置。风扇曲线暂不支持编辑。",
        "TurboModeSupport" => "在性能模式中选择狂暴模式，需接通电源且当前配置已验证。",
        "ColorCalibrationSupport" => "暂不支持修改显示色彩。", "DGpuDirectConnectionSupport" => "暂不支持切换显卡连接方式。",
        "OcSettingsSupport" => "暂不支持修改功耗或超频参数。", "RGBLightbarSupport" => "暂不支持修改灯带效果。",
        "LiquidCoolingSupport" => "暂不支持液冷控制。", "TypeCSupport" => "暂不支持修改 Type-C 供电策略。",
        _ => "目前仅可查看电量，暂不支持设置充电上限。"
    };

    private static string PhaseLabel(MonitorPhase phase) => phase switch
    {
        MonitorPhase.Monitoring => "正在监测", MonitorPhase.Connecting => "正在连接", MonitorPhase.Authenticating => "正在验证连接",
        MonitorPhase.Subscribing or MonitorPhase.WaitingCapabilities => "正在获取设备状态", MonitorPhase.Stopped => "已断开", _ => "尚未连接"
    };

    private static string TopicLabel(string topic) => topic switch
    {
        "Customize/SupportInfo" => "功能支持", "Fan/Status" => "模式配置", "Tray/Status" => "原厂模式状态",
        "System/CpuInfo" => "处理器", "System/GpuInfo" => "显卡", "System/FanInfo" => "风扇", "System/BatteryInfo" => "电池", _ => topic
    };
}
