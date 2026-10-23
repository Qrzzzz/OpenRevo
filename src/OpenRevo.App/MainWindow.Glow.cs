using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using Windows.Foundation;
using Windows.UI;

namespace OpenRevo.App;

public sealed partial class MainWindow
{
    private readonly Grid _metricStage = new();
    private readonly Grid _metricGlowHost = new() { IsHitTestVisible = false };
    private readonly TextBlock _glowCaption = Secondary("");
    private readonly List<MetricGlow> _metricGlows = [];
    private ContainerVisual? _glowContainer;
    private bool _glowRunning;
    private bool _windowActive = true;
    private bool _glowDecorated;
    private string _glowVisualKey = "";

    private sealed class MetricGlow(SpriteVisual visual, CompositionRadialGradientBrush halo)
    {
        internal SpriteVisual Visual { get; } = visual;
        internal CompositionRadialGradientBrush Halo { get; } = halo;
        internal LinearGradientBrush Surface { get; } = new() { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = { new GradientStop { Offset = 0 }, new GradientStop { Offset = .6 }, new GradientStop { Offset = 1 } } };
        internal SolidColorBrush Stroke { get; } = new();
        internal GlowRgb? Color;
    }

    private UIElement GlowToolbar()
    {
        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _glowCaption.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_glowCaption);
        var open = new Button { Content = "卡片光效", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetHelpText(open, "选择预设配色或状态显示，并设置动态流光。");
        Grid.SetColumn(open, 1); bar.Children.Add(open);
        var panel = new StackPanel { Spacing = 14, Width = 320 };
        panel.Children.Add(Text("卡片光效", 18, true));
        var enabled = new ToggleSwitch { Header = "柔和光晕", IsOn = _preferences.GlowEnabled, OnContent = "开启", OffContent = "关闭" };
        panel.Children.Add(enabled);
        var display = new ComboBox { Header = "显示方式", HorizontalAlignment = HorizontalAlignment.Stretch };
        display.Items.Add(new ComboBoxItem { Content = "预设配色" });
        display.Items.Add(new ComboBoxItem { Content = "状态显示" });
        display.Items.Add(new ComboBoxItem { Content = "自定义方案 · 后续版本", IsEnabled = false });
        display.SelectedIndex = (int)_preferences.GlowDisplay;
        LucideIcons.Combo(display); panel.Children.Add(display);
        var preset = new ComboBox { Header = "配色方案", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var palette in Enum.GetValues<GlowPalette>()) preset.Items.Add(GlowAppearance.Name(palette));
        preset.SelectedIndex = (int)_preferences.GlowPalette;
        LucideIcons.Combo(preset); panel.Children.Add(preset);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        AutomationProperties.SetAccessibilityView(swatches, AccessibilityView.Raw);
        panel.Children.Add(swatches);
        var description = Secondary(""); panel.Children.Add(description);
        var motion = new ToggleSwitch { Header = "动态流光", IsOn = _preferences.GlowMotion, OnContent = "开启", OffContent = "关闭" };
        panel.Children.Add(motion);
        panel.Children.Add(Secondary("开启动态会增加 GPU 使用和耗电。仅概览处于前台时流动，并跟随系统动画设置。"));
        panel.Children.Add(Secondary("自定义方案将在后续版本开放。"));
        void UpdateChoices()
        {
            var status = _preferences.GlowDisplay == GlowDisplay.Status;
            display.IsEnabled = enabled.IsOn;
            preset.IsEnabled = enabled.IsOn;
            preset.Visibility = swatches.Visibility = status ? Visibility.Collapsed : Visibility.Visible;
            motion.IsEnabled = enabled.IsOn;
            description.Text = status
                ? "蓝色为常态，紫色过渡至珊瑚红：CPU／GPU 按占用，风扇按输出，电池按低电量显示。当前模式有效时为蓝色；过期、缺失或断开后为灰色。颜色仅辅助查看，不代表故障报警。"
                : "整组卡片共用一片柔和色雾，不同色彩在背景中自然融合。卡片保留轻微染色，仅主读数使用呼应的主题色。";
            swatches.Children.Clear();
            foreach (var color in GlowAppearance.Colors(_preferences.GlowPalette))
                swatches.Children.Add(new Border { Width = 42, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(GlowColor(color)) });
        }
        void Changed()
        {
            _preferences = _preferences with { GlowEnabled = enabled.IsOn, GlowDisplay = display.SelectedIndex == 1 ? GlowDisplay.Status : GlowDisplay.Preset,
                GlowPalette = (GlowPalette)Math.Max(0, preset.SelectedIndex), GlowMotion = motion.IsOn };
            UpdateChoices(); SavePreferences(); RenderMetricGlows();
        }
        enabled.Toggled += (_, _) => Changed(); display.SelectionChanged += (_, _) => Changed();
        preset.SelectionChanged += (_, _) => Changed(); motion.Toggled += (_, _) => Changed();
        UpdateChoices();
        open.Flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 590, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        return bar;
    }

    private void InitializeMetricGlows()
    {
        if (_glowContainer is not null || _closed) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_metricGlowHost).Compositor;
        _glowContainer = compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(_metricGlowHost, _glowContainer);
        foreach (var card in _metricCards)
        {
            var brush = compositor.CreateRadialGradientBrush();
            brush.EllipseCenter = new Vector2(.5f);
            brush.EllipseRadius = new Vector2(.5f);
            foreach (var offset in new[] { 0f, .35f, .7f, 1f }) brush.ColorStops.Add(compositor.CreateColorGradientStop(offset, Microsoft.UI.Colors.Transparent));
            var visual = compositor.CreateSpriteVisual(); visual.Brush = brush; visual.Opacity = .85f;
            _glowContainer.Children.InsertAtTop(visual);
            _metricGlows.Add(new MetricGlow(visual, brush));
            card.SizeChanged += (_, _) => LayoutMetricGlows();
        }
        _metricStage.SizeChanged += (_, _) => LayoutMetricGlows();
        RenderMetricGlows(); LayoutMetricGlows();
    }

    private void LayoutMetricGlows()
    {
        if (_closed || !_metricGlowHost.IsLoaded) return;
        for (var i = 0; i < _metricGlows.Count; i++)
        {
            var card = _metricCards[i];
            var position = card.TransformToVisual(_metricGlowHost).TransformPoint(new Point());
            // Broad, overlapping color fields for the entire group, rather than
            // a ring tracking each card's rectangle. Narrow layouts still blend vertically.
            var width = (float)Math.Max(card.ActualWidth * 2.3, _metricStage.ActualWidth * .92);
            var height = (float)Math.Max(card.ActualHeight * 3.4, _metricStage.ActualHeight * .85);
            _metricGlows[i].Visual.Size = new Vector2(width, height);
            _metricGlows[i].Visual.Offset = new Vector3((float)(position.X + card.ActualWidth / 2) - width / 2,
                (float)(position.Y + card.ActualHeight / 2) - height / 2, 0);
        }
    }

    private static Color GlowColor(GlowRgb rgb, byte alpha = 255) => Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);

    private GlowRgb[] StatusGlowColors()
    {
        bool Current(TopicSample? sample) => sample?.Freshness(TimeProvider.System, _connected) == SampleFreshness.Current;
        var cpu = Sample("System/CpuInfo"); var gpu = Sample("System/GpuInfo");
        var fan = Sample("System/FanInfo"); var battery = Sample("System/BatteryInfo"); var mode = Sample("Fan/Status");
        return [GlowAppearance.LoadColor(cpu?.Number("CpuUsage"), Current(cpu)),
            GlowAppearance.LoadColor(gpu?.Number("GpuUsage"), Current(gpu)),
            GlowAppearance.BatteryColor(battery?.Number("BatteryLifePercent"), Current(battery)),
            GlowAppearance.LoadColor(fan?.Number("CpuFanDuty"), Current(fan)),
            GlowAppearance.LoadColor(fan?.Number("GpuFanDuty"), Current(fan)),
            Current(mode) && _observer?.Pilot.CurrentMode is not null ? GlowAppearance.Blue : GlowAppearance.Muted];
    }

    private void RenderMetricGlows()
    {
        if (_closed) { SetGlowMotion(false); return; }
        var highContrast = _themeSettings?.HighContrast == true;
        var enabled = _preferences.GlowEnabled && !highContrast;
        var animate = GlowAppearance.Animate(enabled, _preferences.GlowMotion, _page == "overview",
            AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized },
            _windowActive, _uiSettings.AnimationsEnabled, highContrast);
        SetGlowMotion(animate);
        _glowCaption.Text = highContrast ? "系统对比度主题 · 光效已暂停" : !enabled ? "卡片光效已关闭" :
            (_preferences.GlowDisplay == GlowDisplay.Status ? "状态配色 · 蓝 → 紫 → 珊瑚红" : GlowAppearance.Name(_preferences.GlowPalette))
                + (_preferences.GlowMotion ? animate ? " · 动态流光" : " · 动态已暂停" : " · 静态光晕");
        var light = _root.ActualTheme == ElementTheme.Light;
        var colors = _preferences.GlowDisplay == GlowDisplay.Status ? StatusGlowColors() : GlowAppearance.Colors(_preferences.GlowPalette);
        // Apply independently of glow caching: a fresh value can become stale
        // while a preset hue remains unchanged, or the glow can be disabled.
        var cardKeys = new[] { "cpu", "gpu", "battery", "cpuFan", "gpuFan", "mode" };
        for (var i = 0; i < cardKeys.Length; i++)
            if (_metricViews.TryGetValue(cardKeys[i], out var cardView))
                cardView.ApplyTheme(new MetricCardTheme(colors[i], light, enabled && colors[i] != GlowAppearance.Muted), enabled);
        if (_glowContainer is null) return;
        _glowContainer.IsVisible = enabled && _page == "overview";
        if (!enabled)
        {
            if (_glowDecorated)
                foreach (var card in _metricCards) { card.ClearValue(Border.BackgroundProperty); card.ClearValue(Border.BorderBrushProperty); }
            _glowDecorated = false; _glowVisualKey = "";
            return;
        }
        if (_page != "overview") return;
        var key = $"{light}:{_preferences.GlowDisplay}:{_preferences.GlowPalette}";
        if (_glowVisualKey != key) { foreach (var glow in _metricGlows) glow.Color = null; _glowVisualKey = key; }
        for (var i = 0; i < _metricGlows.Count; i++)
        {
            var glow = _metricGlows[i]; var color = colors[i];
            if (!_glowDecorated) { _metricCards[i].Background = glow.Surface; _metricCards[i].BorderBrush = glow.Stroke; }
            if (glow.Color == color) continue;
            glow.Color = color;
            var muted = _preferences.GlowDisplay == GlowDisplay.Status && color == GlowAppearance.Muted;
            var strength = muted ? 12 : light ? 54 : 64;
            for (var stop = 0; stop < 4; stop++) glow.Halo.ColorStops[stop].Color = GlowColor(color, (byte)(strength * new[] { 1, .8, .3, 0 }[stop]));
            var surface = GlowRgb.Hex(light ? 0xFBFBFEu : 0x25272Eu);
            // Translucent, near-neutral surfaces let the same background continue
            // through each card; colored outlines would split it into six separate tiles.
            glow.Surface.GradientStops[0].Color = GlowColor(surface.Mix(color, muted ? .015 : .045), light ? (byte)224 : (byte)205);
            glow.Surface.GradientStops[1].Color = GlowColor(surface.Mix(color, .015), light ? (byte)236 : (byte)222);
            glow.Surface.GradientStops[2].Color = GlowColor(surface.Mix(color, muted ? .015 : .03), light ? (byte)224 : (byte)210);
            glow.Stroke.Color = light ? Color.FromArgb(100, 255, 255, 255) : Color.FromArgb(18, 240, 240, 255);
        }
        _glowDecorated = true;
    }

    private void SetGlowMotion(bool running)
    {
        if (_glowContainer is null || _glowRunning == running) return;
        _glowRunning = running;
        for (var i = 0; i < _metricGlows.Count; i++)
        {
            var glow = _metricGlows[i];
            if (running)
            {
                using var drift = _glowContainer.Compositor.CreateVector2KeyFrameAnimation();
                drift.Duration = TimeSpan.FromSeconds(18 + i * 2); drift.IterationBehavior = AnimationIterationBehavior.Forever;
                drift.InsertKeyFrame(0, new Vector2(.46f, .48f)); drift.InsertKeyFrame(.33f, new Vector2(.58f, .40f));
                drift.InsertKeyFrame(.66f, new Vector2(.43f, .60f)); drift.InsertKeyFrame(1, new Vector2(.46f, .48f));
                glow.Halo.StartAnimation("EllipseCenter", drift);
                using var breathe = _glowContainer.Compositor.CreateScalarKeyFrameAnimation();
                breathe.Duration = TimeSpan.FromSeconds(12 + i * 2); breathe.IterationBehavior = AnimationIterationBehavior.Forever;
                breathe.InsertKeyFrame(0, .65f); breathe.InsertKeyFrame(.5f, 1); breathe.InsertKeyFrame(1, .65f);
                glow.Visual.StartAnimation("Opacity", breathe);
            }
            else
            {
                glow.Halo.StopAnimation("EllipseCenter"); glow.Visual.StopAnimation("Opacity");
                glow.Halo.EllipseCenter = new Vector2(.5f); glow.Visual.Opacity = .85f;
            }
        }
    }

    private void DisposeMetricGlows()
    {
        SetGlowMotion(false);
        foreach (var card in _metricViews.Values) card.DisposeWatermark();
        ElementCompositionPreview.SetElementChildVisual(_metricGlowHost, null);
        foreach (var glow in _metricGlows) { glow.Visual.Dispose(); glow.Halo.Dispose(); }
        _metricGlows.Clear(); _glowContainer?.Dispose(); _glowContainer = null;
    }
}
