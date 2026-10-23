using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace OpenRevo.App;

internal sealed partial class MetricCardView
{
    private readonly Canvas _watermarkHost = new() { IsHitTestVisible = false };
    private readonly TextBlock _watermarkSource = new() { FontFamily = LucideIcons.Family, FontSize = 188,
        Width = 216, Height = 240, IsTextScaleFactorEnabled = false };
    private SpriteVisual? _watermarkVisual;
    private DropShadow? _watermarkBlur;
    private CompositionBrush? _watermarkAlpha;
    private CompositionMaskBrush? _watermarkMask;
    private CompositionLinearGradientBrush? _watermarkFade;
    private CompositionRoundedRectangleGeometry? _watermarkBounds;
    private CompositionGeometricClip? _watermarkClip;
    private bool _watermarkEnabled;

    private Canvas CreateWatermark(string glyph)
    {
        _watermarkSource.Text = LucideGlyphs.All[glyph];
        // Keep the glyph's alpha source laid out, outside the clipped viewport.
        // The visible watermark is its soft, masked composition shadow only.
        Canvas.SetLeft(_watermarkSource, -400);
        _watermarkHost.Children.Add(_watermarkSource);
        AutomationProperties.SetAccessibilityView(_watermarkHost, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_watermarkSource, AccessibilityView.Raw);
        _watermarkHost.Loaded += (_, _) => InitializeWatermark();
        _watermarkHost.Unloaded += (_, _) => DisposeWatermark();
        _watermarkHost.SizeChanged += (_, _) => LayoutWatermark();
        return _watermarkHost;
    }

    private void InitializeWatermark()
    {
        if (_watermarkVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_watermarkHost).Compositor;
        _watermarkAlpha = _watermarkSource.GetAlphaMask();
        _watermarkFade = compositor.CreateLinearGradientBrush();
        _watermarkFade.StartPoint = new Vector2(0, 1); _watermarkFade.EndPoint = new Vector2(1, 0);
        _watermarkFade.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        _watermarkFade.ColorStops.Add(compositor.CreateColorGradientStop(.5f, Color.FromArgb(140, 255, 255, 255)));
        _watermarkFade.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(255, 255, 255, 255)));
        _watermarkMask = compositor.CreateMaskBrush();
        _watermarkMask.Source = _watermarkAlpha; _watermarkMask.Mask = _watermarkFade;
        _watermarkBlur = compositor.CreateDropShadow();
        _watermarkBlur.Mask = _watermarkMask; _watermarkBlur.BlurRadius = 10;
        _watermarkBlur.Offset = Vector3.Zero;
        _watermarkVisual = compositor.CreateSpriteVisual();
        _watermarkVisual.Size = new Vector2(216, 240); _watermarkVisual.Shadow = _watermarkBlur;
        _watermarkBounds = compositor.CreateRoundedRectangleGeometry();
        _watermarkBounds.CornerRadius = new Vector2(20);
        _watermarkClip = compositor.CreateGeometricClip(_watermarkBounds);
        ElementCompositionPreview.GetElementVisual(_watermarkHost).Clip = _watermarkClip;
        ElementCompositionPreview.SetElementChildVisual(_watermarkHost, _watermarkVisual);
        LayoutWatermark(); UpdateWatermark(_theme, _watermarkEnabled);
    }

    private void LayoutWatermark()
    {
        if (_watermarkVisual is null || _watermarkBounds is null) return;
        _watermarkBounds.Size = new Vector2((float)_watermarkHost.ActualWidth, (float)_watermarkHost.ActualHeight);
        // The right and upper edges disappear behind the rounded card boundary.
        _watermarkVisual.Offset = new Vector3((float)_watermarkHost.ActualWidth - 142, -42, 0);
    }

    private void UpdateWatermark(MetricCardTheme theme, bool enabled)
    {
        _watermarkEnabled = enabled;
        if (_watermarkVisual is null || _watermarkBlur is null) return;
        _watermarkVisual.IsVisible = enabled;
        var color = theme.Accent;
        _watermarkBlur.Color = Color.FromArgb(255, color.R, color.G, color.B);
        _watermarkBlur.Opacity = theme.Light ? .24f : .20f;
    }

    internal void DisposeWatermark()
    {
        ElementCompositionPreview.SetElementChildVisual(_watermarkHost, null);
        ElementCompositionPreview.GetElementVisual(_watermarkHost).Clip = null;
        _watermarkVisual?.Dispose(); _watermarkVisual = null;
        _watermarkBlur?.Dispose(); _watermarkBlur = null;
        _watermarkMask?.Dispose(); _watermarkMask = null;
        // The alpha brush is supplied by XAML; release our reference so the
        // same TextBlock can supply it again when this page is reloaded.
        _watermarkAlpha = null;
        _watermarkFade?.Dispose(); _watermarkFade = null;
        _watermarkClip?.Dispose(); _watermarkClip = null;
        _watermarkBounds?.Dispose(); _watermarkBounds = null;
    }
}
