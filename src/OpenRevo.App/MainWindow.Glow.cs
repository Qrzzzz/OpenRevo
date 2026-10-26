using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private Button? _glowButton;
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

    private FrameworkElement GlowToolbar()
    {
        var open = new Button { Content = "卡片光效", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _glowButton = open;
        AutomationProperties.SetHelpText(open, "预览四组配色或按运行状态着色，设置动态流光。");
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(Text("卡片光效", UiStyle.ModuleTitle, true));
        var enabled = new ToggleSwitch { IsOn = _preferences.GlowEnabled };
        panel.Children.Add(SettingRow("光晕与水印", "关闭后使用普通卡片", enabled));
        var display = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var presetDisplay = new RadioButton { Content = "预设主题", GroupName = "glow-display" };
        var statusDisplay = new RadioButton { Content = "运行状态", GroupName = "glow-display" };
        display.Children.Add(presetDisplay); display.Children.Add(statusDisplay); panel.Children.Add(display);
        var previews = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        previews.ColumnDefinitions.Add(new ColumnDefinition()); previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); previews.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var choices = new List<(GlowPalette Palette, ToggleButton Button)>();
        foreach (var palette in Enum.GetValues<GlowPalette>())
        {
            var content = new StackPanel { Spacing = 7 };
            content.Children.Add(Text(GlowAppearance.Name(palette), 13, true));
            var swatches = new Grid { Height = 9, ColumnSpacing = 2 };
            var colors = GlowAppearance.Colors(palette);
            for (var i = 0; i < colors.Length; i++)
            {
                swatches.ColumnDefinitions.Add(new ColumnDefinition());
                var swatch = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(GlowColor(colors[i])) };
                AutomationProperties.SetAccessibilityView(swatch, AccessibilityView.Raw);
                Grid.SetColumn(swatch, i); swatches.Children.Add(swatch);
            }
            content.Children.Add(swatches);
            var choice = new ToggleButton { Content = content, Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 62 };
            AutomationProperties.SetName(choice, GlowAppearance.Name(palette));
            Grid.SetColumn(choice, (int)palette % 2); Grid.SetRow(choice, (int)palette / 2); previews.Children.Add(choice);
            choices.Add((palette, choice));
        }
        panel.Children.Add(previews);
        var description = Secondary("CPU／GPU 按占用率，风扇按输出，电池按电量。蓝 → 紫 → 珊瑚红；颜色不表示温度告警。");
        panel.Children.Add(description);
        panel.Children.Add(new Button { Content = "自定义方案 · 后续版本", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var motion = new ToggleSwitch { IsOn = _preferences.GlowMotion };
        panel.Children.Add(SettingRow("动态流光", "增加 GPU 使用与耗电，仅概览前台运行。", motion));
        var selected = _preferences.GlowPalette;
        var updating = false;
        void UpdateChoices()
        {
            updating = true;
            var status = _preferences.GlowDisplay == GlowDisplay.Status;
            presetDisplay.IsChecked = !status; statusDisplay.IsChecked = status;
            presetDisplay.IsEnabled = statusDisplay.IsEnabled = enabled.IsOn; motion.IsEnabled = enabled.IsOn;
            previews.Visibility = status ? Visibility.Collapsed : Visibility.Visible;
            description.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (palette, button) in choices) { button.IsEnabled = enabled.IsOn; button.IsChecked = palette == selected; }
            updating = false;
        }
        void Changed()
        {
            if (updating) return;
            _preferences = _preferences with { GlowEnabled = enabled.IsOn,
                GlowDisplay = statusDisplay.IsChecked == true ? GlowDisplay.Status : GlowDisplay.Preset,
                GlowPalette = selected, GlowMotion = motion.IsOn };
            UpdateChoices(); SavePreferences(); RenderMetricGlows();
        }
        foreach (var (palette, button) in choices) button.Click += (_, _) => { selected = palette; Changed(); };
        enabled.Toggled += (_, _) => Changed(); presetDisplay.Checked += (_, _) => Changed();
        statusDisplay.Checked += (_, _) => Changed(); motion.Toggled += (_, _) => Changed();
        UpdateChoices();
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 500, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var flyout = new Flyout { Content = scroll, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, ShouldConstrainToRootBounds = true };
        flyout.Opening += (_, _) => scroll.MaxHeight = Math.Clamp(_root.ActualHeight - 80, 220, 500);
        open.Flyout = flyout;
        return open;
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
        if (_glowButton is not null) ToolTipService.SetToolTip(_glowButton, _glowCaption.Text);
        var light = _root.ActualTheme == ElementTheme.Light;
        var colors = _preferences.GlowDisplay == GlowDisplay.Status ? StatusGlowColors() : GlowAppearance.Colors(_preferences.GlowPalette);
        // Apply independently of glow caching: a fresh value can become stale
        // while a preset hue remains unchanged, or the glow can be disabled.
        var cardKeys = new[] { "cpu", "gpu", "battery", "cpuFan", "gpuFan", "mode" };
        for (var i = 0; i < cardKeys.Length; i++)
            if (_metricViews.TryGetValue(cardKeys[i], out var cardView))
                cardView.ApplyTheme(new MetricCardTheme(colors[i], light, enabled && colors[i] != GlowAppearance.Muted, _preferences.GlowDisplay == GlowDisplay.Status), enabled);
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
