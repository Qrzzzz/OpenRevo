using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using Windows.UI;

namespace OpenRevo.App;

internal sealed partial class MetricCardView
{
    internal Border Surface { get; }
    private readonly string _label;
    private readonly bool _statusInDetail;
    private readonly bool _mode;
    private bool _inkInDetail;
    private MetricDetail _detail;
    private readonly TextBlock _value;
    private readonly TextBlock _unit = Secondary(18);
    private readonly TextBlock _detailLabel = Secondary(14);
    private readonly TextBlock _detailValue = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _timestamp = Secondary(12);
    private MetricReading _reading;
    private SampleFreshness? _freshness;
    private MetricCardTheme _theme;
    private GlowRgb? _ink;
    private readonly SolidColorBrush _primaryBrush = new();

    internal MetricCardView(string label, string glyph, bool mode, bool statusInDetail)
    {
        _label = label; _mode = mode;
        _statusInDetail = statusInDetail;
        var layout = new StackPanel();
        var title = Secondary(15); title.Text = label; title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        layout.Children.Add(title);

        var reading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, MinHeight = 60, Margin = new Thickness(0, 3, 0, 0) };
        _value = new TextBlock { Text = "—", FontSize = mode ? UiStyle.ModeReading : UiStyle.Reading, FontWeight = FontWeights.Bold,
            CharacterSpacing = -25, VerticalAlignment = VerticalAlignment.Bottom };
        _unit.FontWeight = FontWeights.SemiBold;
        _unit.VerticalAlignment = VerticalAlignment.Bottom; _unit.Margin = new Thickness(0, 0, 0, mode ? 6 : 8);
        // The combined primary reading already supplies the spoken unit.
        AutomationProperties.SetAccessibilityView(_unit, AccessibilityView.Raw);
        reading.Children.Add(_value); reading.Children.Add(_unit);
        layout.Children.Add(reading);
        _timestamp.Margin = new Thickness(0, 1, 0, 0);
        layout.Children.Add(_timestamp);

        var divider = new Border { Height = 1, BorderThickness = new Thickness(0, 1, 0, 0), Opacity = .65, Margin = new Thickness(0, 12, 0, 10),
            Style = (Style)Application.Current.Resources["SurfaceCardStyle"] };
        AutomationProperties.SetAccessibilityView(divider, AccessibilityView.Raw);
        layout.Children.Add(divider);
        var footer = new Grid { ColumnSpacing = 14, MinHeight = 26 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _detailLabel.VerticalAlignment = VerticalAlignment.Center;
        _detailLabel.TextWrapping = TextWrapping.Wrap;
        _detailValue.TextAlignment = TextAlignment.Right;
        _detailValue.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_detailValue, 1);
        footer.Children.Add(_detailLabel); footer.Children.Add(_detailValue);
        layout.Children.Add(footer);
        var layers = new Grid();
        layers.Children.Add(CreateWatermark(glyph));
        layout.Margin = new Thickness(UiStyle.CardPadding); layers.Children.Add(layout);
        Surface = new Border { Child = layers, MinHeight = 190,
            CornerRadius = new CornerRadius(UiStyle.MetricRadius), BorderThickness = new Thickness(1),
            Style = (Style)Application.Current.Resources["SurfaceCardStyle"] };
        AutomationProperties.SetName(Surface, label);
        _value.SizeChanged += (_, _) => LayoutWatermark();
        _unit.SizeChanged += (_, _) => LayoutWatermark();
        if (mode)
        {
            var link = new HyperlinkButton { Content = "切换模式 ›", Padding = new Thickness(0), MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(link, "切换模式，打开性能与散热");
            link.Click += (_, _) => NavigateRequested?.Invoke();
            _detailValue.Visibility = Visibility.Collapsed;
            Grid.SetColumn(link, 1); footer.Children.Add(link);
        }
    }

    internal event Action? NavigateRequested;

    private static TextBlock Secondary(double size) => new() { FontSize = size,
        Style = (Style)Application.Current.Resources["SecondaryBodyStyle"] };

    internal void SetContent(MetricReading reading, MetricDetail detail, SampleFreshness? freshness, TimeSpan age)
    {
        _reading = reading; _detail = detail; _freshness = freshness;
        _value.Text = reading.Value; _unit.Text = reading.Unit;
        _unit.Visibility = reading.Unit.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _detailLabel.Text = detail.Label; _detailValue.Text = detail.Value;
        _timestamp.Text = UiPresentation.MetricTime(freshness, age, reading.Available);
        AutomationProperties.SetName(_value, $"{_label}，{reading.Value} {reading.Unit}，{detail.Label} {detail.Value}，{_timestamp.Text}");
        UpdateInk();
    }

    internal void ApplyTheme(MetricCardTheme theme, bool watermarkEnabled)
    {
        _theme = theme; UpdateInk(); UpdateWatermark(theme, watermarkEnabled);
    }

    private void UpdateInk()
    {
        var detailEmphasis = _theme.Status && _statusInDetail;
        _detailLabel.Text = (_mode ? _detail.Value : _detail.Label) + (detailEmphasis ? " · 着色依据" : "");
        var ink = _theme.PrimaryInk(detailEmphasis ? _detail.Value != "—" : _reading.Available, _freshness);
        if (_theme.Status && _statusInDetail) _value.ClearValue(TextBlock.ForegroundProperty);
        else _detailValue.ClearValue(TextBlock.ForegroundProperty);
        if (_ink == ink && _inkInDetail == detailEmphasis) return;
        _ink = ink; _inkInDetail = detailEmphasis;
        if (ink is { } color)
        {
            _primaryBrush.Color = Color.FromArgb(255, color.R, color.G, color.B);
            if (detailEmphasis) _detailValue.Foreground = _primaryBrush;
            else _value.Foreground = _primaryBrush;
        }
        else { _value.ClearValue(TextBlock.ForegroundProperty); _detailValue.ClearValue(TextBlock.ForegroundProperty); }
    }
}
