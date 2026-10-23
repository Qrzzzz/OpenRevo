using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenRevo.App;

internal static class LucideIcons
{
    internal static FontFamily Family { get; } = new("ms-appx:///Assets/Lucide.ttf#lucide");
    internal static FontIcon Icon(string name, double size = 20)
    {
        var icon = new FontIcon { FontFamily = Family, Glyph = LucideGlyphs.All[name], FontSize = size, IsTextScaleFactorEnabled = false };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }
    internal static FontIconSource Source(string name, double size = 18) => new()
    { FontFamily = Family, Glyph = LucideGlyphs.All[name], FontSize = size, IsTextScaleFactorEnabled = false };

    internal static void Status(InfoBar bar)
    {
        var name = bar.Severity switch
        { InfoBarSeverity.Success => "circle-check", InfoBarSeverity.Warning => "triangle-alert", InfoBarSeverity.Error => "circle-alert", _ => "info" };
        if (bar.IconSource is not FontIconSource font || font.Glyph != LucideGlyphs.All[name]) bar.IconSource = Source(name);
    }

    // Retain native controls, focus and accessibility; replace only their decorative chevrons.
    internal static void Chevron(Control control, string part, string name)
    {
        var icon = Find<AnimatedIcon>(control, part);
        if (icon is null) return;
        icon.Source = null;
        icon.FallbackIconSource = Source(name, 16);
        icon.Width = icon.Height = 16;
    }
    internal static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name) return match;
            if (Find<T>(child, name) is { } nested) return nested;
        }
        return null;
    }
    internal static void Combo(ComboBox combo) => combo.Loaded += (_, _) => Chevron(combo, "DropDownGlyph", "chevron-down");
    internal static void Expander(Expander expander)
    {
        void Refresh() => Chevron(expander, "ExpandCollapseChevron", expander.IsExpanded ? "chevron-up" : "chevron-down");
        expander.Loaded += (_, _) => Refresh();
        expander.Expanding += (_, _) => Refresh();
        expander.Collapsed += (_, _) => Refresh();
    }
}
