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

public sealed partial class MainWindow : Window
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
    private readonly Button _officeMode = new() { MinWidth = 0 };
    private readonly Button _balancedMode = new() { MinWidth = 0 };
    private readonly Button _turboMode = new() { MinWidth = 0 };
    private readonly Button _customMode = new() { MinWidth = 0 };
    private readonly Button _restoreMode = new() { Content = "撤销上次切换" };
    private readonly Button _keepMode = new() { Content = "保留此模式" };
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
    private readonly Button _refresh = new() { Content = "刷新数据", MinWidth = 88 };
    private readonly StackPanel _capabilities = new() { Spacing = 8 };
    private readonly ComboBox _topic = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _rawLive = new() { Header = "自动更新", OnContent = "更新中", OffContent = "已暂停", IsOn = true };
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
        _root.Style = (Style)Application.Current.Resources["RootSurfaceStyle"];
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        _navigation.MenuItems.Add(Nav("性能模式", "pilot", Symbol.Play));
        _navigation.MenuItems.Add(Nav("功能支持", "capabilities", Symbol.AllApps));
        _navigation.FooterMenuItems.Add(Nav("原始数据", "raw", Symbol.Document));
        _navigation.FooterMenuItems.Add(Nav("设置与诊断", "settings", Symbol.Setting));
        _navigation.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && item.Tag is string tag) ShowPage(tag);
        };
        var chrome = ConnectionChrome(); Grid.SetRow(chrome, 1); _root.Children.Add(chrome);
        Grid.SetRow(_navigation, 2);
        _root.Children.Add(_navigation);
        _pages["overview"] = OverviewPage();
        _pages["capabilities"] = CapabilitiesPage();
        _pages["raw"] = RawPage();
        _pages["settings"] = SettingsPage();
        _pages["pilot"] = PilotPage();
        _navigation.DisplayModeChanged += (_, _) => LayoutPages();
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
        ToolTipService.SetToolTip(_refresh, "重新读取设备状态 (Ctrl+R)");
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
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(24, 20, 24, 28), MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heading = new StackPanel { Spacing = 6 };
        var titleText = Text(title, 28, true); AutomationProperties.SetHeadingLevel(titleText, AutomationHeadingLevel.Level1);
        heading.Children.Add(titleText); heading.Children.Add(Secondary(subtitle)); panel.Children.Add(heading);
        return panel;
    }

    private UIElement OverviewPage()
    {
        var panel = Section("设备概览", "查看温度、风扇、电量和当前性能模式。");
        AddMetric("cpu", "处理器温度", "\uE950");
        AddMetric("gpu", "显卡温度", "\uE7F4");
        AddMetric("battery", "电池电量", "\uE83F");
        AddMetric("cpuFan", "CPU 风扇", "\uE9CA");
        AddMetric("gpuFan", "GPU 风扇", "\uE9CA");
        AddMetric("mode", "当前模式", "\uE945");
        panel.Children.Add(_metrics);
        panel.Children.Add(_sensorNotice);
        var config = new StackPanel { Spacing = 10 };
        config.Children.Add(Secondary("以下是模式设定值，并非当前实际功耗。"));
        config.Children.Add(_configuration);
        config.Children.Add(_rangeWarning);
        var modeLink = new HyperlinkButton { Content = "更改性能模式", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        modeLink.Click += (_, _) => _navigation.SelectedItem = _navigation.MenuItems[1];
        panel.Children.Add(modeLink);
        panel.Children.Add(Details("查看模式功耗设定", config));
        panel.Children.Add(Secondary("数据按原厂服务的频率更新，未更新或断开后的数值会单独标记。"));
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void AddMetric(string key, string label, string glyph)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 }); header.Children.Add(Text(label));
        var value = Text("—", 30, true);
        var detail = Secondary("等待数据");
        var age = Secondary("尚未收到状态");
        panel.Children.Add(header); panel.Children.Add(value); panel.Children.Add(detail); panel.Children.Add(age);
        var card = Card(panel); card.MinHeight = 150;
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
        var panel = Section("功能支持", "查看 OpenRevo 已开放的功能，以及原厂报告的其他能力。");
        panel.Children.Add(_capabilities);
        var details = new StackPanel { Spacing = 12 }; details.Children.Add(_capAge); details.Children.Add(_capReview);
        panel.Children.Add(Details("查看功能识别详情", details));
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement RawPage()
    {
        var panel = new Grid { Margin = new Thickness(28, 20, 28, 28), RowSpacing = 16 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            panel.RowDefinitions.Add(new RowDefinition { Height = height });
        panel.Children.Add(Text("原始数据", 28, true));
        var guidance = Secondary("用于排查问题。暂停画面更新后，可选择并复制内容，设备监测会继续。"); Grid.SetRow(guidance, 1); panel.Children.Add(guidance);
        var controls = new Grid { ColumnSpacing = 16 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _topic.Header = "数据来源";
        foreach (var topic in ReadProtocol.Topics) _topic.Items.Add(new ComboBoxItem { Content = TopicLabel(topic), Tag = topic });
        _topic.SelectedIndex = 0;
        AutomationProperties.SetName(_topic, "状态主题");
        AutomationProperties.SetName(_raw, "厂商原始状态 JSON");
        _topic.SelectionChanged += (_, _) => { _rawKey = ""; RenderRaw(force: true); };
        _rawLive.Toggled += (_, _) => RenderRaw(force: true);
        controls.Children.Add(_topic); Grid.SetColumn(_rawLive, 1); controls.Children.Add(_rawLive);
        Grid.SetRow(controls, 2); panel.Children.Add(controls);
        Grid.SetRow(_rawMeta, 3); panel.Children.Add(_rawMeta);
        Grid.SetRow(_raw, 4); panel.Children.Add(_raw);
        return panel;
    }

    private UIElement SettingsPage()
    {
        var panel = Section("设置与诊断", $"OpenRevo {AppVersion.Current} · 适用于当前已验证的机型与后台版本");
        var theme = new ComboBox { Header = "应用主题", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色"); theme.SelectedIndex = 0;
        theme.SelectionChanged += (_, _) => _root.RequestedTheme = theme.SelectedIndex switch
        { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        panel.Children.Add(theme);
        var identity = new StackPanel { Spacing = 12 };
        identity.Children.Add(Text("设备与连接", 18, true)); identity.Children.Add(_deviceSummary);
        _identity.IsTextSelectionEnabled = true;
        identity.Children.Add(Details("设备与后台版本详情", _identity)); panel.Children.Add(Card(identity));
        var review = new StackPanel { Spacing = 12 };
        review.Children.Add(Text("排查连接问题", 18, true));
        review.Children.Add(Secondary("重新检测前请先断开连接。若问题仍存在，可保存报告以便检查。"));
        review.Children.Add(_reinspect);
        _auditHint.IsTextSelectionEnabled = true;
        AutomationProperties.SetLiveSetting(_auditHint, AutomationLiveSetting.Polite);
        var saveAudit = new Button { Content = "保存诊断报告" };
        saveAudit.Click += (_, _) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo", "audit-review.json");
                DiagnosticFile.Write(path, Diagnostics());
                _auditHint.Text = "报告已保存到：\n" + path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _auditHint.Text = "无法保存报告，请确认本地目录可写。"; }
        };
        review.Children.Add(saveAudit); review.Children.Add(_auditHint); panel.Children.Add(Card(review));
        var policy = new StackPanel { Spacing = 12 };
        policy.Children.Add(Text("需保留原厂后台服务。版本不匹配时，请查看设备详情并保存报告；新机型或新版本需要重新验证。"));
        policy.Children.Add(Text("重新检测和保存报告不会自动批准新的机型、版本或配置。报告仅保存本机，不会自动上传。"));
        policy.Children.Add(Text("已开放状态监测和四种性能模式。风扇曲线、电池充电限制、屏幕色彩和功耗参数暂不支持修改。"));
        policy.Children.Add(Secondary("断开只结束 OpenRevo 的连接，不停止原厂后台采样。关闭后保留 last-session.json，不恢复或重放模式命令。"));
        panel.Children.Add(Details("适用范围与重新验证", policy));
        var diagnostics = new StackPanel { Spacing = 12 };
        diagnostics.Children.Add(_diagnosticText);
        panel.Children.Add(Details("查看会话详情", diagnostics));
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement PilotPage()
    {
        var panel = Section("性能模式", "选择适合当前任务的性能与散热策略，切换需接通电源。");
        var current = new StackPanel { Spacing = 8 };
        current.Children.Add(Secondary("当前模式")); current.Children.Add(_currentModeTitle); current.Children.Add(_currentModeDetail);
        panel.Children.Add(Card(current));
        panel.Children.Add(_modeFeedback);
        AutomationProperties.SetName(_modeProgress, "正在核对模式切换结果");
        panel.Children.Add(_modeProgress);
        ConfigureModeChoices(); panel.Children.Add(_modeChoices);
        _keepMode.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ToolTipService.SetToolTip(_keepMode, "结束本次恢复记录，继续使用当前模式；不发送切换命令");
        ToolTipService.SetToolTip(_restoreMode, "恢复最近一次切换前的模式和配置");
        _modeCompletion.Children.Add(_keepMode); _modeCompletion.Children.Add(_restoreMode); panel.Children.Add(_modeCompletion);
        AutomationProperties.SetLiveSetting(_modeResult, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_modeFeedback, AutomationLiveSetting.Polite);
        var state = new StackPanel { Spacing = 12 };
        state.Children.Add(_pilotState); state.Children.Add(_modeResult); state.Children.Add(_pilotBaseline);
        state.Children.Add(_controlReadiness); state.Children.Add(_previousPilot);
        panel.Children.Add(Details("切换记录与配置详情", state));
        var help = new StackPanel { Spacing = 12 };
        help.Children.Add(Text("可以连续切换。每次以切换前的配置作为撤销目标，撤销只影响最近一次切换。保留此模式会结束本次恢复记录，不发送硬件命令。"));
        help.Children.Add(Text("自定义模式使用原厂已保存的配置，本版不编辑参数。原厂程序仍可能改变配置；结果无法确认时，请在原厂控制中心核对。关闭、断线或重连不会自动恢复模式。"));
        help.Children.Add(Secondary("Tab / Shift+Tab 移动焦点，Enter / 空格操作按钮；Ctrl+R 刷新，Ctrl+S 导出。"));
        panel.Children.Add(Details("了解切换与撤销", help));
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
        _lastModeResult = result;
        _modeResult.Text = (result.Succeeded ? "完成：" : "未完成：") + result.Detail;
        if (result.Succeeded) _previousPilot.Text = "当前操作已写入本地记录；旧操作不会重放。";
        RenderPresentation();
        FrameworkElementAutomationPeer.CreatePeerForElement(_modeFeedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
            _lastModeResult = null;
            _modeResult.Text = restore ? "正在恢复切换前的配置…" : "正在切换，请稍候…";
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
        _lastModeResult = null; _modeResult.Text = "";
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
                    _identity.Text = $"{id.Manufacturer} {id.Model}\n主板：{id.Board}\nBIOS：{id.Bios}\n机型验证：{MatchLabel(inspection.DeviceMatch)}\n后台验证：{MatchLabel(inspection.FileMatch)}\n" + FormatDifferences(inspection.Differences ?? []);
                    _device.Text = DeviceCaption(id);
                }
            }
            _device.Text = DeviceCaption(_installation.Device);
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
                if (failure is not null) { _error.Severity = failure.Code == DiagnosticCode.SystemSuspended ? InfoBarSeverity.Informational : InfoBarSeverity.Warning; _error.Title = DiagnosticTitle(failure.Code); _error.Message = InterfaceCopy(failure.Message + "\n" + failure.NextStep); _error.IsOpen = true; }
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
        { SampleFreshness.Cached => "缓存数据", SampleFreshness.Stale => "暂未更新", SampleFreshness.Disconnected => "已断开 · 历史数据", _ => "已更新" };
        return $"{prefix} · {(seconds < 60 ? $"{seconds:0} 秒前" : $"{seconds / 60:0} 分钟前")}";
    }

    private void UpdateMetric(string key, TopicSample? sample, string value, string detail)
    {
        var text = _metricText[key]; text.Value.Text = value; text.Detail.Text = detail; text.Age.Text = Age(sample);
        text.Value.Opacity = 1;
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
            _pilotBaseline.Text = journal.Baseline is null ? "尚未切换模式；切换前会自动记录原配置。" :
                $"切换前：{DeviceAudit.ModeName(journal.BaselineMode)}\nCPU 设定：PL1 {journal.Baseline.Text("CPU_PL1")} / PL2 {journal.Baseline.Text("CPU_PL2")}\nGPU TGP 设定：{journal.Baseline.Text("GPU_ConfigurableTGPTarget")}\n风扇策略：{journal.Baseline.Text("FAN_TableName")}";
        }
        var latest = _diagnostics.LastOrDefault();
        var missing = ReadProtocol.Topics.Where(topic => !_samples.ContainsKey(topic)).Select(TopicLabel).ToArray();
        _diagnosticText.Text = $"连接状态：{PhaseLabel(_observer?.Phase ?? MonitorPhase.Idle)}\n本次连接：{health?.DurationSeconds / 60:0.#} 分钟\n已接收 {_received} 条消息\n" + (missing.Length == 0 ? "已收到全部数据来源\n" : $"等待数据：{string.Join("、", missing)}\n") +
            (latest is null ? "暂无异常" : $"最近诊断：{latest.Code}\n{latest.Message}\n{latest.NextStep}");
        var cpu = Sample("System/CpuInfo"); var gpu = Sample("System/GpuInfo");
        var fan = Sample("System/FanInfo"); var battery = Sample("System/BatteryInfo"); var mode = Sample("Fan/Status");
        UpdateMetric("cpu", cpu, Number(cpu, "CpuTemperature", " °C", -20, 150), $"占用 {Number(cpu, "CpuUsage", "%", 0, 100)}");
        UpdateMetric("gpu", gpu, Number(gpu, "GpuTemperature", " °C", -20, 150), $"占用 {Number(gpu, "GpuUsage", "%", 0, 100)}");
        UpdateMetric("battery", battery, Number(battery, "BatteryLifePercent", "%", 0, 100), mode?.Text("IsAC") switch
        { "True" or "true" => "已接通电源", "False" or "false" => "使用电池供电", _ => "供电状态待确认" });
        UpdateMetric("cpuFan", fan, Number(fan, "CpuFanRpm", " RPM", 0, 20000), $"风扇输出 {Number(fan, "CpuFanDuty", "%", 0, 100)}");
        UpdateMetric("gpuFan", fan, Number(fan, "GpuFanRpm", " RPM", 0, 20000), $"风扇输出 {Number(fan, "GpuFanDuty", "%", 0, 100)}");
        var effectiveMode = mode?.Text("OperatingMode") ?? pilot?.CurrentMode;
        UpdateMetric("mode", mode, DeviceAudit.ModeName(effectiveMode), mode?.Text("OperatingMode") is null && effectiveMode is not null
            ? "根据已验证的配置识别" : "可在性能模式中更改");
        _configuration.Text = mode is null ? "等待厂商配置状态" : $"CPU PL1  {mode.Text("CPU_PL1") ?? "—"}    PL2  {mode.Text("CPU_PL2") ?? "—"}    PL4  {mode.Text("CPU_PL4") ?? "—"}\nGPU TGP  {mode.Text("GPU_ConfigurableTGPTarget") ?? "—"}    DynamicBoost  {mode.Text("GPU_DynamicBoost") ?? "—"}\n{Age(mode)}";
        var warning = DeviceAudit.TgpInconsistency(mode); _rangeWarning.IsOpen = warning is not null; _rangeWarning.Message = warning ?? "";
        RenderPresentation();
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
        var other = new StackPanel { Spacing = 12 };
        foreach (var capability in DeviceAudit.Capabilities(sample, _connected))
        {
            var implemented = capability.Field is "SystemMonitorSupport" or "FanSettingsSupport" or "TurboModeSupport";
            var current = sample?.Freshness(TimeProvider.System, _connected) == SampleFreshness.Current;
            var availability = !current ? (_connected ? "等待更新" : "上次报告") :
                !implemented ? "尚未开放" : capability.DeclaredSupported is null ? "待确认" : capability.DeclaredSupported == false ? "未报告支持" :
                capability.Field == "SystemMonitorSupport" ? "可监测" : "模式已开放";
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(Text($"{capability.Name} · {availability}", 16, true));
            content.Children.Add(Secondary(FeatureDescription(capability.Field)));
            ToolTipService.SetToolTip(content, $"原厂字段：{capability.Field} = {sample?.Text(capability.Field) ?? "未知"}\n{capability.Detail}");
            if (implemented) _capabilities.Children.Add(Card(content)); else other.Children.Add(content);
        }
        _capabilities.Children.Add(Details("尚未开放的功能", other));
    }

    private void RenderRaw(bool force = false)
    {
        if (_topic.SelectedItem is not ComboBoxItem { Tag: string topic }) return;
        var sample = Sample(topic);
        if (!_rawLive.IsOn && !force) return;
        _rawMeta.Text = sample is null ? "正在等待此来源的数据" : !_rawLive.IsOn
            ? $"{topic} · 画面已暂停 · 快照接收于 {sample.ReceivedAtUtc.ToLocalTime():HH:mm:ss}"
            : $"{topic} · {Age(sample)} · 接收于 {sample.ReceivedAtUtc.ToLocalTime():HH:mm:ss}";
        var key = topic + sample?.Payload.GetRawText();
        if (_rawKey == key && !force) return;
        _rawKey = key; _raw.Text = sample is null ? "尚未收到此主题的数据。" : JsonSerializer.Serialize(sample.Payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatDifferences(IReadOnlyList<AuditDifference> differences) => differences.Count == 0 ? "无已知差异" :
        string.Join("\n", differences.Take(8).Select(d => $"{d.Field}：已验证值 {d.Expected}；实际值 {d.Observed}"))
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
            if (file is not null && !_closed)
            {
                await FileIO.WriteTextAsync(file, Diagnostics());
                _notice.Title = "诊断已导出"; _notice.Message = file.Path; _notice.IsOpen = true;
            }
        }
        catch (Exception)
        {
            var diagnostic = MonitoringDiagnostic.Create(DiagnosticCode.ExportFailed); AddDiagnostic(diagnostic);
            _error.Title = "无法导出诊断"; _error.Message = InterfaceCopy(diagnostic.NextStep); _error.IsOpen = true;
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
