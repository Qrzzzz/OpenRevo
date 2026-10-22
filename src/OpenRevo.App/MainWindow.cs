using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using System.Runtime.InteropServices;
using PowerManager = Microsoft.Windows.System.Power.PowerManager;
using Microsoft.Windows.System.Power;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Text;

namespace OpenRevo.App;

public sealed class MainWindow : Window
{
    private readonly Grid _root = new();
    private readonly NavigationView _navigation = new();
    private readonly Grid _metrics = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly List<Border> _metricCards = [];
    private readonly Dictionary<string, (TextBlock Value, TextBlock Detail, TextBlock Age)> _metricText = [];
    private readonly Dictionary<string, TopicSample> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UIElement> _pages = [];
    private readonly TextBlock _connection = Text("尚未连接", 13);
    private readonly TextBlock _device = Text("正在识别本机设备", 14);
    private readonly TextBlock _configuration = Text("等待厂商配置状态", 14);
    private readonly TextBlock _identity = Text("正在读取安装与机型信息", 14);
    private readonly TextBlock _controlReadiness = Text("等待识别", 13);
    private readonly TextBlock _auditHint = Text("", 13);
    private readonly Button _reinspect = new() { Content = "重新检测并连接" };
    private readonly TextBlock _capAge = Text("等待能力响应", 13);
    private readonly TextBlock _capReview = Text("等待能力身份核对", 13);
    private readonly TextBlock _sensorNotice = Text("", 13);
    private readonly Queue<SessionHealthSnapshot> _previousSessions = new();
    private bool _powerNotificationsAvailable;
    private readonly TextBlock _diagnosticText = Text("尚无异常诊断", 13);
    private readonly List<MonitoringDiagnostic> _diagnostics = [];
    private readonly Button _officeMode = new() { Content = "切到办公", MinWidth = 112 };
    private readonly Button _balancedMode = new() { Content = "切到均衡", MinWidth = 112 };
    private readonly Button _turboMode = new() { Content = "切到狂暴", MinWidth = 112 };
    private readonly Button _customMode = new() { Content = "切到自定义", MinWidth = 120 };
    private readonly Button _restoreMode = new() { Content = "恢复基线", MinWidth = 124 };
    private readonly Button _keepMode = new() { Content = "保留当前模式", MinWidth = 124 };
    private readonly TextBlock _modeResult = Text("", 14);
    private readonly TextBlock _pilotState = Text("等待本机实时状态", 14);
    private readonly TextBlock _pilotBaseline = Text("尚未记录切换基线", 13);
    private readonly TextBlock _previousPilot = Text("", 13);
    private static string PilotJournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo", "mode-pilot-journal.json");
    private InstallationReport? _inspection;
    private bool _exporting;
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning };
    private readonly InfoBar _rangeWarning = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "配置范围不一致" };
    private readonly Button _connect = new() { Content = "断开", MinWidth = 88 };
    private readonly Button _refresh = new() { Content = "刷新", MinWidth = 88 };
    private readonly StackPanel _capabilities = new() { Spacing = 8 };
    private readonly ComboBox _topic = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _rawLive = new() { Header = "实时更新", IsOn = true };
    private readonly TextBlock _rawMeta = Text("尚无数据", 13);
    private readonly TextBox _raw = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), MinHeight = 320, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private BridgeInstallation? _installation;
    private BridgeObserver? _observer;
    private CancellationTokenSource? _stop;
    private Task? _session;
    private bool _connected;
    private bool _closed;
    private string _page = "overview";
    private string _capabilityKey = "";
    private string _rawKey = "";
    private long _received;
    private int _generation;

    public MainWindow()
    {
        Title = "OpenRevo";
        SystemBackdrop = new MicaBackdrop();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Grid { Height = 44, Padding = new Thickness(16, 0, 140, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 17 });
        brand.Children.Add(Text("OpenRevo", 14, true));
        brand.Children.Add(Text($"预览版 {AppVersion.Current}", 12));
        titleBar.Children.Add(brand);
        _root.Children.Add(titleBar);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        _root.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        _navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _navigation.IsSettingsVisible = false;
        _navigation.IsTitleBarAutoPaddingEnabled = false;
        _navigation.OpenPaneLength = 204;
        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _navigation.CompactModeThresholdWidth = 800;
        _navigation.ExpandedModeThresholdWidth = 1050;
        _navigation.MenuItems.Add(Nav("设备概览", "overview", Symbol.Home));
        _navigation.MenuItems.Add(Nav("硬件能力", "capabilities", Symbol.AllApps));
        _navigation.MenuItems.Add(Nav("原始状态", "raw", Symbol.Document));
        _navigation.MenuItems.Add(Nav("模式控制", "pilot", Symbol.Play));
        _navigation.FooterMenuItems.Add(Nav("连接与外观", "settings", Symbol.Setting));
        _navigation.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && item.Tag is string tag) ShowPage(tag);
        };
        Grid.SetRow(_navigation, 1);
        _root.Children.Add(_navigation);
        _pages["overview"] = OverviewPage();
        _pages["capabilities"] = CapabilitiesPage();
        _pages["raw"] = RawPage();
        _pages["settings"] = SettingsPage();
        _pages["pilot"] = PilotPage();
        Content = _root;
        _navigation.SelectedItem = _navigation.MenuItems[0];
        _metrics.SizeChanged += (_, _) => LayoutMetrics();
        _connect.Click += async (_, _) =>
        {
            _connect.IsEnabled = false;
            if (_session is { IsCompleted: false }) { _stop?.Cancel(); await _session; }
            else StartSession();
            if (!_closed) _connect.IsEnabled = true;
        };
        _refresh.Click += (_, _) => _observer?.RequestRefresh();
        _reinspect.Click += (_, _) =>
        {
            if (_session is { IsCompleted: false }) return;
            _modeResult.Text = "";
            StartSession();
        };
        _officeMode.Click += async (_, _) => await OperateModeAsync(PilotMode.Office, false);
        _balancedMode.Click += async (_, _) => await OperateModeAsync(PilotMode.Balanced, false);
        _turboMode.Click += async (_, _) => await OperateModeAsync(PilotMode.Turbo, false);
        _customMode.Click += async (_, _) => await OperateModeAsync(PilotMode.Custom, false);
        _restoreMode.Click += async (_, _) => await OperateModeAsync(default, true);
        _keepMode.Click += (_, _) =>
        {
            if (_observer is null || !_connected) return;
            var result = _observer.Pilot.KeepCurrent();
            ShowModeResult(result);
            Render();
            FocusModeAction();
        };
        ToolTipService.SetToolTip(_refresh, "重新查询状态并请求传感器采样 (Ctrl+R)");
        _timer.Tick += (_, _) => Render();
        _timer.Start();
        _root.Loaded += (_, _) =>
        {
            SizeForCurrentDisplay(); UpdateCaptionColors(); EnablePowerNotifications(); StartSession();
            // Establish XAML focus before the first keyboard accelerator.
            DispatcherQueue.TryEnqueue(() => { if (!_closed) ((NavigationViewItem)_navigation.MenuItems[0]).Focus(FocusState.Programmatic); });
        };
        Closed += (_, _) => { _closed = true; _connected = false; _timer.Stop(); DisablePowerNotifications(); _stop?.Cancel(); SaveLastSession(); };
        var refreshShortcut = new KeyboardAccelerator { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control };
        refreshShortcut.Invoked += (_, e) => { if (_connected) _observer?.RequestRefresh(); e.Handled = true; };
        _root.KeyboardAccelerators.Add(refreshShortcut);
        var saveShortcut = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        saveShortcut.Invoked += async (_, e) => { e.Handled = true; await ExportAsync(); };
        _root.KeyboardAccelerators.Add(saveShortcut);
    }

    private static NavigationViewItem Nav(string label, string tag, Symbol symbol) => new()
    { Content = label, Tag = tag, Icon = new SymbolIcon(symbol) };

    private void SizeForCurrentDisplay()
    {
        var scale = _root.XamlRoot?.RasterizationScale ?? 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1180 * scale), Math.Max(640, area.Width - (int)(64 * scale)));
        var height = Math.Min((int)(820 * scale), Math.Max(400, area.Height - (int)(64 * scale)));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(width, (int)(720 * scale));
            presenter.PreferredMinimumHeight = Math.Min(height, (int)(440 * scale));
        }
    }

    private void UpdateCaptionColors()
    {
        var light = _root.ActualTheme == ElementTheme.Light;
        var foreground = light ? Colors.Black : Colors.White;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = light
            ? Windows.UI.Color.FromArgb(255, 96, 96, 96) : Windows.UI.Color.FromArgb(255, 170, 170, 170);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private static TextBlock Text(string text, double size = 14, bool semibold = false) => new()
    { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal };

    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(20), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Style = (Style)Application.Current.Resources["SurfaceCardStyle"]
    };

    private static StackPanel Section(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(28, 20, 28, 28) };
        panel.Children.Add(Text(title, 28, true));
        panel.Children.Add(Text(subtitle, 14));
        return panel;
    }

    private UIElement OverviewPage()
    {
        var panel = Section("设备概览", "查看本机状态，硬件控制仍由厂商后台管理。");
        var connectionBar = new Grid { ColumnSpacing = 8 };
        connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(_connection); status.Children.Add(_device);
        connectionBar.Children.Add(status);
        Grid.SetColumn(_refresh, 1); connectionBar.Children.Add(_refresh);
        Grid.SetColumn(_connect, 2); connectionBar.Children.Add(_connect);
        panel.Children.Add(connectionBar);
        panel.Children.Add(_error);
        AddMetric("cpu", "处理器温度", "\uE950");
        AddMetric("gpu", "显卡温度", "\uE7F4");
        AddMetric("battery", "电池电量", "\uE83F");
        AddMetric("cpuFan", "CPU 风扇", "\uE9CA");
        AddMetric("gpuFan", "GPU 风扇", "\uE9CA");
        AddMetric("mode", "当前模式", "\uE945");
        panel.Children.Add(_metrics);
        panel.Children.Add(_sensorNotice);
        var config = new StackPanel { Spacing = 10 };
        config.Children.Add(Text("当前功耗配置", 16, true));
        config.Children.Add(_configuration);
        config.Children.Add(Text("以上为厂商返回的配置原值，不代表实时功耗。", 12));
        config.Children.Add(_rangeWarning);
        panel.Children.Add(Card(config));
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Text("传感器按厂商服务的频率更新；缓存和旧数据会单独标记。", 12));
        var export = new Button { Content = "导出诊断", VerticalAlignment = VerticalAlignment.Top };
        export.Click += async (_, _) => await ExportAsync();
        ToolTipService.SetToolTip(export, "保存本地 JSON；不包含认证信息 (Ctrl+S)");
        Grid.SetColumn(export, 1); footer.Children.Add(export); panel.Children.Add(footer);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void AddMetric(string key, string label, string glyph)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 }); header.Children.Add(Text(label));
        var value = Text("—", 30, true);
        var detail = Text("等待数据", 13);
        var age = Text("尚未收到状态", 12);
        panel.Children.Add(header); panel.Children.Add(value); panel.Children.Add(detail); panel.Children.Add(age);
        var card = Card(panel); card.MinHeight = 174;
        AutomationProperties.SetName(card, label);
        _metricText[key] = (value, detail, age); _metricCards.Add(card); _metrics.Children.Add(card);
    }

    private void LayoutMetrics()
    {
        var count = _metrics.ActualWidth >= 850 ? 3 : _metrics.ActualWidth >= 500 ? 2 : 1;
        if (_metrics.ColumnDefinitions.Count == count) return;
        _metrics.ColumnDefinitions.Clear(); _metrics.RowDefinitions.Clear();
        for (var i = 0; i < count; i++) _metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (_metricCards.Count + count - 1) / count; i++) _metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _metricCards.Count; i++) { Grid.SetColumn(_metricCards[i], i % count); Grid.SetRow(_metricCards[i], i / count); }
    }

    private UIElement CapabilitiesPage()
    {
        var panel = Section("硬件能力", "能力声明来自本机厂商服务，开放范围依据当前审计证据。");
        panel.Children.Add(_capAge); panel.Children.Add(_capReview); panel.Children.Add(_capabilities);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement RawPage()
    {
        var panel = new Grid { Margin = new Thickness(28, 20, 28, 28), RowSpacing = 16 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        panel.Children.Add(Text("原始状态", 28, true));
        var controls = new Grid { ColumnSpacing = 16 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var topic in ReadProtocol.Topics) _topic.Items.Add(topic);
        _topic.SelectedIndex = 0;
        AutomationProperties.SetName(_topic, "状态主题");
        AutomationProperties.SetName(_raw, "厂商原始状态 JSON");
        _topic.SelectionChanged += (_, _) => { _rawKey = ""; RenderRaw(force: true); };
        _rawLive.Toggled += (_, _) => { if (_rawLive.IsOn) RenderRaw(force: true); };
        controls.Children.Add(_topic); Grid.SetColumn(_rawLive, 1); controls.Children.Add(_rawLive);
        Grid.SetRow(controls, 1); panel.Children.Add(controls);
        Grid.SetRow(_rawMeta, 2); panel.Children.Add(_rawMeta);
        Grid.SetRow(_raw, 3); panel.Children.Add(_raw);
        return panel;
    }

    private UIElement SettingsPage()
    {
        var panel = Section("连接与外观", $"OpenRevo {AppVersion.Current} · Windows 11 原生界面");
        var theme = new ComboBox { Header = "应用主题", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色"); theme.SelectedIndex = 0;
        theme.SelectionChanged += (_, _) => _root.RequestedTheme = theme.SelectedIndex switch
        { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        panel.Children.Add(theme);
        var identity = new StackPanel { Spacing = 12 };
        identity.Children.Add(Text("本机识别", 18, true)); identity.Children.Add(_identity); panel.Children.Add(Card(identity));
        var review = new StackPanel { Spacing = 12 };
        review.Children.Add(Text("版本差异与重新审计", 18, true));
        review.Children.Add(Text("机型／BIOS 和后台版本分别核对。版本不匹配时查看下方实际值与审计值，保存材料供重新审计；恢复受支持版本后，可重新检测并连接。检测或保存材料不会自动批准新机型、新版本或配置。", 13));
        review.Children.Add(_reinspect);
        var saveAudit = new Button { Content = "保存审计材料" };
        saveAudit.Click += (_, _) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo", "audit-review.json");
                DiagnosticFile.Write(path, Diagnostics());
                _auditHint.Text = "已保存：" + path + "。提交审计前可检查内容；程序不会上传或加载该文件以解除限制。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _auditHint.Text = "审计材料保存失败，请确认本地目录可写。"; }
        };
        review.Children.Add(saveAudit); review.Children.Add(_auditHint); panel.Children.Add(Card(review));
        var policy = new StackPanel { Spacing = 12 };
        policy.Children.Add(Text("连接范围", 18, true));
        policy.Children.Add(Text("仅连接本机厂商后台。启动时核对主板、BIOS 与服务文件，版本不匹配时停止连接。"));
        policy.Children.Add(Text("监测会启用厂商后台的共享采样计时器。断开只结束本应用的连接，不发送全局停止请求，以免影响原厂监控。"));
        policy.Children.Add(Text("当前开放状态读取、诊断导出与四模式控制。通知与时间戳不参与控制配置比对，供电及模式单独核对。风扇曲线、电池阈值、屏幕色彩、功耗和超频写入尚未开放。"));
        panel.Children.Add(Card(policy));
        var diagnostics = new StackPanel { Spacing = 12 };
        diagnostics.Children.Add(Text("连接诊断", 18, true));
        diagnostics.Children.Add(_diagnosticText);
        diagnostics.Children.Add(Text("关闭窗口会保存 last-session.json；导出诊断包含识别结果、会话统计、主题收包数、供电与休眠事件。", 12));
        panel.Children.Add(Card(diagnostics));
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement PilotPage()
    {
        var panel = Section("模式控制", "支持连续切换办公、均衡、狂暴和原厂已保存的自定义配置。");
        panel.Children.Add(Text("切换会联动功耗与风扇策略。需接通电源，且当前配置与本机审计记录匹配。", 14));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_officeMode); actions.Children.Add(_balancedMode); actions.Children.Add(_turboMode); actions.Children.Add(_customMode);
        panel.Children.Add(actions);
        var completion = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        completion.Children.Add(_keepMode); completion.Children.Add(_restoreMode); panel.Children.Add(completion);
        AutomationProperties.SetLiveSetting(_modeResult, AutomationLiveSetting.Polite);
        panel.Children.Add(_modeResult);
        panel.Children.Add(Text("自定义使用原厂当前已保存的配置；本版不编辑配置参数或风扇曲线。", 13));
        var state = new StackPanel { Spacing = 12 };
        state.Children.Add(_pilotState); state.Children.Add(_pilotBaseline); panel.Children.Add(Card(state));
        panel.Children.Add(_controlReadiness);
        panel.Children.Add(Text("可直接继续切换，每次以切换前的配置作为新的基线。保留当前模式不发送命令，并结束本次恢复记录；恢复基线只撤销最近一次切换。关闭、断线和重连均不会自动恢复。", 13));
        panel.Children.Add(Text("使用 Tab / Shift+Tab 移动焦点，Enter 或空格操作按钮；Ctrl+R 刷新，Ctrl+S 导出诊断。", 13));
        panel.Children.Add(Text("原厂程序仍可能改变配置。出现结果不明或外部变化时停止写入，请用原厂界面核对保存的基线。", 13));
        panel.Children.Add(_previousPilot);
        try
        {
            if (File.Exists(PilotJournalPath) && new FileInfo(PilotJournalPath).Length <= 65536)
            {
                _previousPilot.Text = PreviousModeJournal.Describe(File.ReadAllText(PilotJournalPath));
            }
            else if (File.Exists(PilotJournalPath)) _previousPilot.Text = "旧模式记录过大，未读取；请核对当前模式。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { _previousPilot.Text = "无法读取上次恢复记录；请在原厂界面核对当前配置。"; }
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void ShowModeResult(ModePilotResult result)
    {
        _modeResult.Text = (result.Succeeded ? "完成：" : "未完成：") + result.Detail;
        if (result.Succeeded) _previousPilot.Text = "当前操作已写入本地记录；旧操作不会重放。";
        FrameworkElementAutomationPeer.CreatePeerForElement(_modeResult)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FocusModeAction()
    {
        var button = new[] { _keepMode, _officeMode, _balancedMode, _turboMode, _customMode }.FirstOrDefault(b => b.IsEnabled);
        if (button is not null) button.Focus(FocusState.Programmatic);
        else _navigation.Focus(FocusState.Programmatic);
    }

    private async Task OperateModeAsync(PilotMode mode, bool restore)
    {
        var observer = _observer;
        if (observer is null || !_connected) return;
        try
        {
            _modeResult.Text = restore ? "正在恢复最近一次基线…" : "正在切换，请等待回读…";
            var operation = restore ? observer.Pilot.RestoreAsync() : observer.Pilot.SwitchAsync(mode);
            Render();
            var result = await operation;
            if (_closed || observer != _observer) return;
            ShowModeResult(result);
            Render();
            FocusModeAction();
        }
        catch (Exception exception)
        {
            var diagnostic = MonitoringDiagnostic.From(exception); AddDiagnostic(diagnostic);
            ShowModeResult(new(false, "模式操作未完成；请用原厂界面核对基线。", null)); Render();
        }
    }

    private void ShowPage(string tag)
    {
        _page = tag; _navigation.Content = _pages[tag]; Render();
    }

    private void EnablePowerNotifications()
    {
        try
        {
            PowerManager.PowerSourceKindChanged += PowerSourceChanged;
            PowerManager.SystemSuspendStatusChanged += SuspendStatusChanged;
            _powerNotificationsAvailable = true;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            DisablePowerNotifications();
            AddDiagnostic(MonitoringDiagnostic.Create(DiagnosticCode.PowerNotificationsUnavailable));
        }
    }
    private void DisablePowerNotifications()
    {
        PowerManager.PowerSourceKindChanged -= PowerSourceChanged;
        PowerManager.SystemSuspendStatusChanged -= SuspendStatusChanged;
    }
    private void PowerSourceChanged(object? sender, object args)
    {
        _observer?.NotifyPowerSource(PowerManager.PowerSourceKind == PowerSourceKind.AC);
        DispatcherQueue.TryEnqueue(() => { if (!_closed) Render(); });
    }
    private void SuspendStatusChanged(object? sender, object args)
    {
        var status = PowerManager.SystemSuspendStatus;
        if (status == SystemSuspendStatus.Entering) _observer?.NotifySuspend();
        else if (status is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume) _observer?.NotifyResume();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (status is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
                _connection.Text = _observer?.Phase == MonitorPhase.Monitoring ? "系统已唤醒 · 监测中" : "系统已唤醒；需要时点击重新连接";
            Render(); SaveLastSession();
        });
    }
    private void StartSession()
    {
        if (_closed || _session is { IsCompleted: false }) return;
        _stop?.Dispose(); _stop = new CancellationTokenSource();
        if (_observer is not null)
        {
            _previousSessions.Enqueue(_observer.Health.Snapshot(false));
            while (_previousSessions.Count > 8) _previousSessions.Dequeue();
        }
        _observer = new BridgeObserver();
        if (_powerNotificationsAvailable) _observer.NotifyPowerSource(PowerManager.PowerSourceKind == PowerSourceKind.AC);
        _connected = false; _samples.Clear(); _received = 0;
        _observer.Pilot.SetJournalWriter(journal => DiagnosticFile.Write(PilotJournalPath,
            JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } })));
        _installation = null; _inspection = null; _identity.Text = "正在读取本机身份与安装指纹…";
        _capabilityKey = ""; _rawKey = "";
        _error.IsOpen = false; _error.Severity = InfoBarSeverity.Warning; _connection.Text = "正在连接本机服务…"; _connect.Content = "断开";
        _session = RunSessionAsync(_observer, ++_generation, _stop.Token);
    }

    private async Task RunSessionAsync(BridgeObserver observer, int generation, CancellationToken token)
    {
        MonitoringDiagnostic? failure = null;
        try
        {
            _ = ProtocolEvidence.Registry;
            InstallationReport? inspection = null;
            try { _installation = await Task.Run(() => AuditedInstallation.Inspect(report => inspection = report), token); }
            finally
            {
                _inspection = inspection;
                if (inspection is not null)
                {
                    var id = inspection.Device;
                    _identity.Text = $"{id.Manufacturer} {id.Model}\n主板  {id.Board}\nBIOS  {id.Bios}\n配置  {inspection.ProfileId}\n身份匹配  {inspection.DeviceMatch}\n文件匹配  {inspection.FileMatch}\n" + FormatDifferences(inspection.Differences ?? []);
                    _device.Text = $"{id.Model} · {id.Bios}";
                }
            }
            _device.Text = $"{_installation.Device.Model} · {_installation.Device.Bios}";
            _identity.Text += $"\n桥接文件  {_installation.BridgeSha256[..12]}…\n核心文件  {_installation.ServiceSha256[..12]}…";
            await Task.Run(() => observer.ObserveAsync(_installation, sample => DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || generation != _generation) return;
                _samples[sample.Topic] = sample; _received++; Render();
            }), state => DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || generation != _generation) return; _connected = true; _connection.Text = state;
            }), token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = observer.LastDiagnostic ?? MonitoringDiagnostic.From(ex); AddDiagnostic(failure); }
        finally
        {
            if (!_closed && generation == _generation)
            {
                _connected = false; _connection.Text = failure is null ? "已断开 · 保留上次收到的值" : "连接未完成或已中断";
                _connect.Content = "重新连接"; _connect.IsEnabled = true;
                if (failure is not null) { _error.Severity = failure.Code == DiagnosticCode.SystemSuspended ? InfoBarSeverity.Informational : InfoBarSeverity.Warning; _error.Title = failure.Code == DiagnosticCode.SystemSuspended ? "系统休眠 · 连接已结束" : $"无法继续监测 · {failure.Code}"; _error.Message = failure.Message + "\n" + failure.NextStep; _error.IsOpen = true; }
                Render(); SaveLastSession();
            }
        }
    }

    private TopicSample? Sample(string topic) => _samples.GetValueOrDefault(topic);
    private string Age(TopicSample? sample)
    {
        if (sample is null) return "尚未收到状态";
        var seconds = Math.Max(0, sample.Age().TotalSeconds);
        var prefix = sample.Freshness(TimeProvider.System, _connected) switch
        { SampleFreshness.Cached => "保留缓存", SampleFreshness.Stale => "旧数据", SampleFreshness.Disconnected => "已断开 · 历史值", _ => "已收到" };
        return $"{prefix} · {(seconds < 60 ? $"{seconds:0} 秒前" : $"{seconds / 60:0} 分钟前")}";
    }

    private void UpdateMetric(string key, TopicSample? sample, string value, string detail)
    {
        var text = _metricText[key]; text.Value.Text = value; text.Detail.Text = detail; text.Age.Text = Age(sample);
        text.Value.Opacity = sample?.Freshness(TimeProvider.System, _connected) == SampleFreshness.Current ? 1 : 0.5;
        AutomationProperties.SetName(text.Value, $"{value}，{detail}，{text.Age.Text}");
    }

    private static string Number(TopicSample? sample, string field, string unit, double minimum, double maximum)
    {
        var value = sample?.Number(field);
        return value is not null && value >= minimum && value <= maximum ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + unit : "—";
    }

    private void Render()
    {
        _reinspect.IsEnabled = _session is null || _session.IsCompleted;
        _refresh.IsEnabled = _connected;
        var health = _observer?.Health.Snapshot(_connected);
        _sensorNotice.Text = health?.SensorNotice ?? "";
        _sensorNotice.Visibility = string.IsNullOrEmpty(_sensorNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
        var pilot = _observer?.Pilot;
        var config = pilot?.ConfigurationReview;
        _controlReadiness.Text = (_connected ? pilot?.ControlStatus : "连接未就绪；请在连接与外观查看身份／版本差异。")
            + (config?.Differences.Count > 0 ? "\n" + FormatDifferences(config.Differences) + "\n关键配置变化时，先核对原厂配置并保存审计材料；不会自动采纳当前值。" : "");
        _officeMode.IsEnabled = _connected && pilot?.CanSwitchTo(PilotMode.Office) == true;
        _balancedMode.IsEnabled = _connected && pilot?.CanSwitchTo(PilotMode.Balanced) == true;
        _turboMode.IsEnabled = _connected && pilot?.CanSwitchTo(PilotMode.Turbo) == true;
        _customMode.IsEnabled = _connected && pilot?.CanSwitchTo(PilotMode.Custom) == true;
        _restoreMode.IsEnabled = _connected && pilot?.CanRestore == true;
        _keepMode.IsEnabled = _connected && pilot?.CanKeep == true;
        if (pilot is not null)
        {
            var journal = pilot.Journal;
            _pilotState.Text = (journal.Phase switch
            {
                PilotPhase.Idle => "就绪", PilotPhase.Preparing => "准备", PilotPhase.Sending => "发送中",
                PilotPhase.AwaitingReadback => "等待回读", PilotPhase.Applied => "已切换", PilotPhase.Restoring => "恢复中",
                PilotPhase.Restored => "已恢复", PilotPhase.Kept => "已保留", PilotPhase.Uncertain => "结果不确定", _ => "连接已中断"
            }) + " · " + journal.Detail;
            _pilotBaseline.Text = journal.Baseline is null ? "尚未记录基线。按钮不可用时，请刷新并确认供电、实时能力和已审计配置。" :
                $"原模式：{DeviceAudit.ModeName(journal.BaselineMode)}\n原 CPU 配置：PL1 {journal.Baseline.Text("CPU_PL1")} / PL2 {journal.Baseline.Text("CPU_PL2")}\n原 GPU TGP 配置：{journal.Baseline.Text("GPU_ConfigurableTGPTarget")}\n原风扇表：{journal.Baseline.Text("FAN_TableName")}";
        }
        var latest = _diagnostics.LastOrDefault();
        var missing = ReadProtocol.Topics.Where(topic => !_samples.ContainsKey(topic));
        _diagnosticText.Text = $"会话阶段：{_observer?.Phase ?? MonitorPhase.Idle}\n会话时长：{health?.DurationSeconds / 60:0.#} 分钟\n已接收 {_received} 条消息\n尚未收到：{string.Join("、", missing)}\n" +
            (latest is null ? "暂无异常" : $"最近诊断：{latest.Code}\n{latest.Message}\n{latest.NextStep}");
        var cpu = Sample("System/CpuInfo"); var gpu = Sample("System/GpuInfo");
        var fan = Sample("System/FanInfo"); var battery = Sample("System/BatteryInfo"); var mode = Sample("Fan/Status");
        UpdateMetric("cpu", cpu, Number(cpu, "CpuTemperature", " °C", -20, 150), $"占用 {Number(cpu, "CpuUsage", "%", 0, 100)}");
        UpdateMetric("gpu", gpu, Number(gpu, "GpuTemperature", " °C", -20, 150), $"占用 {Number(gpu, "GpuUsage", "%", 0, 100)}");
        UpdateMetric("battery", battery, Number(battery, "BatteryLifePercent", "%", 0, 100), mode?.Text("IsAC") switch
        { "True" or "true" => "已接通电源", "False" or "false" => "使用电池供电", _ => "供电状态待确认" });
        UpdateMetric("cpuFan", fan, Number(fan, "CpuFanRpm", " RPM", 0, 20000), $"占空比 {Number(fan, "CpuFanDuty", "%", 0, 100)}");
        UpdateMetric("gpuFan", fan, Number(fan, "GpuFanRpm", " RPM", 0, 20000), $"占空比 {Number(fan, "GpuFanDuty", "%", 0, 100)}");
        var effectiveMode = mode?.Text("OperatingMode") ?? pilot?.CurrentMode;
        UpdateMetric("mode", mode, DeviceAudit.ModeName(effectiveMode), mode?.Text("OperatingMode") is null && effectiveMode is not null
            ? "模式由已审计配置识别；原始字段保留 null" : "四模式切换见模式页面");
        _configuration.Text = mode is null ? "等待厂商配置状态" : $"CPU PL1  {mode.Text("CPU_PL1") ?? "—"}    PL2  {mode.Text("CPU_PL2") ?? "—"}    PL4  {mode.Text("CPU_PL4") ?? "—"}\nGPU TGP  {mode.Text("GPU_ConfigurableTGPTarget") ?? "—"}    DynamicBoost  {mode.Text("GPU_DynamicBoost") ?? "—"}\n{Age(mode)}";
        var warning = DeviceAudit.TgpInconsistency(mode); _rangeWarning.IsOpen = warning is not null; _rangeWarning.Message = warning ?? "";
        if (_page == "capabilities") RenderCapabilities();
        if (_page == "raw") RenderRaw();
    }

    private void RenderCapabilities()
    {
        var sample = Sample("Customize/SupportInfo"); _capAge.Text = $"{Age(sample)} · BIOS 项目 {sample?.Text("BIOS_PROJECT_ID") ?? "—"} · 项目号 {sample?.Text("ProjectID") ?? "—"}";
        var differences = AuditReview.Capabilities(sample);
        _capReview.Text = differences.Count == 0 ? "能力响应中的机型身份与监控就绪条件匹配。每项功能仍按独立能力位开放，控制还需关键配置与供电匹配。" : FormatDifferences(differences);
        var key = (sample?.Payload.GetRawText() ?? "none") + sample?.Freshness(TimeProvider.System, _connected);
        if (_capabilityKey == key) return;
        _capabilityKey = key; _capabilities.Children.Clear();
        foreach (var capability in DeviceAudit.Capabilities(sample, _connected))
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(Text($"{capability.Name}  ·  {capability.Availability}", 16, true));
            content.Children.Add(Text(capability.Detail, 13));
            content.Children.Add(Text($"服务原值：{capability.Field} = {sample?.Text(capability.Field) ?? "未知"}", 12));
            _capabilities.Children.Add(Card(content));
        }
    }

    private void RenderRaw(bool force = false)
    {
        if (_topic.SelectedItem is not string topic) return;
        var sample = Sample(topic);
        if (!_rawLive.IsOn && !force) return;
        _rawMeta.Text = sample is null ? "该主题尚未收到数据" : $"{Age(sample)} · 接收时间 {sample.ReceivedAtUtc.ToLocalTime():HH:mm:ss} · RETAIN={sample.Retained.ToString().ToLowerInvariant()}";
        var key = topic + sample?.Payload.GetRawText();
        if (_rawKey == key && !force) return;
        _rawKey = key; _raw.Text = sample is null ? "尚未收到此主题的数据。" : JsonSerializer.Serialize(sample.Payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatDifferences(IReadOnlyList<AuditDifference> differences) => differences.Count == 0 ? "无已知差异" :
        string.Join("\n", differences.Take(8).Select(d => $"{d.Field}：审计值 {d.Expected}；实际值 {d.Observed}"))
        + (differences.Count > 8 ? $"\n另有 {differences.Count - 8} 项，完整差异见审计材料。" : "");

    private string Diagnostics() => JsonSerializer.Serialize(new
    {
        schemaVersion = 4, application = "OpenRevo", version = AppVersion.Current, exportedAtUtc = DateTimeOffset.UtcNow, connected = _connected,
        device = _installation?.Device, bridgeSha256 = _installation?.BridgeSha256, serviceSha256 = _installation?.ServiceSha256,
        inspection = _inspection, phase = _closed ? "Closed" : _observer?.Phase.ToString(), diagnostics = _diagnostics.ToArray(),
        configurationReview = _observer?.Pilot.ConfigurationReview,
        capabilityReview = AuditReview.Capabilities(Sample("Customize/SupportInfo")),
        fieldPolicy = AuditReview.Policy,
        protocolSchemaVersion = ProtocolEvidence.Registry.GetProperty("schemaVersion").GetInt32(),
        sessionHealth = _observer?.Health.Snapshot(_connected), previousSessions = _previousSessions.ToArray(),
        powerNotificationsAvailable = _powerNotificationsAvailable,
        topicHealth = ReadProtocol.Topics.Select(topic => new { topic, received = _samples.ContainsKey(topic),
            freshness = Sample(topic)?.Freshness(TimeProvider.System, _connected).ToString() ?? "Missing",
            ageSeconds = Sample(topic)?.Age().TotalSeconds }).ToArray(),
        hardwareWritesImplemented = ModePilotPolicy.Enabled, directHardwareAccessImplemented = false, receivedCount = _received,
        modePilotImplemented = true, modePilot = _observer?.Pilot.Journal,
        requests = _observer?.Requests, samples = _samples.Values.OrderBy(s => s.Topic).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });

    private async Task ExportAsync()
    {
        if (_exporting || _closed) return;
        _exporting = true;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"OpenRevo-{DateTime.Now:yyyyMMdd-HHmmss}", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("JSON 诊断", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null && !_closed) await FileIO.WriteTextAsync(file, Diagnostics());
        }
        catch (Exception)
        {
            var diagnostic = MonitoringDiagnostic.Create(DiagnosticCode.ExportFailed); AddDiagnostic(diagnostic);
            _error.Title = diagnostic.Message; _error.Message = diagnostic.NextStep; _error.IsOpen = true;
        }
        finally { _exporting = false; }
    }

    private void AddDiagnostic(MonitoringDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
        if (_diagnostics.Count > 64) _diagnostics.RemoveAt(0);
    }

    private void SaveLastSession()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
            Directory.CreateDirectory(directory);
            DiagnosticFile.Write(Path.Combine(directory, "last-session.json"), Diagnostics());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
