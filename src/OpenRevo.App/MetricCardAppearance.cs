using System.Globalization;
using OpenRevo.Core;

namespace OpenRevo.App;

internal readonly record struct MetricReading(string Value, string Unit, bool Available)
{
    internal static MetricReading Numeric(double? value, string unit, double minimum, double maximum, bool grouped = false) =>
        value is not null && double.IsFinite(value.Value) && value >= minimum && value <= maximum
            ? new(value.Value.ToString(grouped ? "#,0" : "0.#", CultureInfo.InvariantCulture), unit, true)
            : new("—", "", false);

    internal static MetricReading Mode(string? mode) => mode switch
    {
        "0" => new("办公", "", true), "1" => new("均衡", "", true),
        "2" => new("狂暴", "", true), "3" => new("自定义", "", true),
        _ => new("待确认", "", false)
    };
}

internal readonly record struct MetricDetail(string Label, string Value);

// Each card receives the same hue as its background field. Only the primary
// reading uses this ink; labels, units and supporting information stay neutral.
internal readonly record struct MetricCardTheme(GlowRgb Accent, bool Light, bool Enabled, bool Status = false)
{
    internal GlowRgb? PrimaryInk(bool available, SampleFreshness? freshness) =>
        Enabled && available && freshness == SampleFreshness.Current ? ReadableInk(Accent, Light, !Status) : null;

    internal static GlowRgb ReadableInk(GlowRgb accent, bool light, bool restrained = true)
    {
        var background = GlowRgb.Hex(light ? 0xFBFBFEu : 0x25272Eu);
        var target = GlowRgb.Hex(light ? 0x181818u : 0xF7F7F7u);
        // Weight and luminance carry the hierarchy. Preserve only a quiet hue
        // in the primary reading instead of six competing pastel headlines.
        if (restrained) accent = accent.Mix(target, light ? .82 : .76);
        for (var step = 0; step <= 100; step++)
        {
            var ink = accent.Mix(target, step / 100d);
            // A little margin for the translucent surface and overlapping glow.
            if (Contrast(ink, background) >= 5.5) return ink;
        }
        return target;
    }

    internal static double Contrast(GlowRgb first, GlowRgb second)
    {
        static double Luminance(GlowRgb color)
        {
            static double Linear(byte channel) => channel / 255d <= .04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    internal static (string Status, string Age) Timestamp(SampleFreshness? freshness, TimeSpan age) => (
        freshness switch { SampleFreshness.Current => "实时", SampleFreshness.Cached => "缓存", SampleFreshness.Stale => "较旧",
            SampleFreshness.Disconnected => "历史", _ => "等待数据" },
        freshness is null ? "尚未收到" : age.TotalSeconds < 60 ? $"{Math.Max(0, age.TotalSeconds):0} 秒前" : $"{age.TotalMinutes:0} 分钟前");
}
