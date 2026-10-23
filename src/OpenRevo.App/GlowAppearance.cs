namespace OpenRevo.App;

internal enum GlowDisplay { Preset, Status }
internal enum GlowPalette { Spectrum, Cyberpunk, Polar, Rose }

internal readonly record struct GlowRgb(byte R, byte G, byte B)
{
    internal static GlowRgb Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    internal GlowRgb Mix(GlowRgb other, double amount) => new(
        (byte)Math.Round(R + (other.R - R) * Math.Clamp(amount, 0, 1)),
        (byte)Math.Round(G + (other.G - G) * Math.Clamp(amount, 0, 1)),
        (byte)Math.Round(B + (other.B - B) * Math.Clamp(amount, 0, 1)));
}

// Presentation only. These colors never feed device authorization or commands.
internal static class GlowAppearance
{
    internal static readonly GlowRgb Muted = GlowRgb.Hex(0x8993A5);
    internal static readonly GlowRgb Blue = GlowRgb.Hex(0x568FFF);
    internal static readonly GlowRgb Violet = GlowRgb.Hex(0xA578EF);
    internal static readonly GlowRgb Coral = GlowRgb.Hex(0xFF587B);
    internal static string Name(GlowPalette palette) => palette switch
    { GlowPalette.Cyberpunk => "赛博朋克", GlowPalette.Polar => "极地微光", GlowPalette.Rose => "玫瑰暮色", _ => "流光彩谱" };

    internal static GlowRgb[] Colors(GlowPalette palette) => (palette switch
    {
        GlowPalette.Cyberpunk => new uint[] { 0x28D9EF, 0xE666CF, 0x9B6CFA, 0x697BFF, 0x36CBE0, 0xDF65C4 },
        GlowPalette.Polar => [0x65B7EF, 0x8A9BEA, 0x70D1CD, 0x85C1EA, 0xB39BE3, 0x73CAD8],
        GlowPalette.Rose => [0xE382A9, 0x9891DB, 0xB7A0E9, 0xA27FCE, 0xCC90BD, 0x798FE0],
        _ => [0xEE738A, 0xEFA268, 0x668FEF, 0xA681EA, 0x7F9FEE, 0xCC7FD2]
    }).Select(GlowRgb.Hex).ToArray();

    internal static GlowRgb LoadColor(double? percent, bool current)
    {
        if (!current || percent is null || !double.IsFinite(percent.Value) || percent < 0 || percent > 100) return Muted;
        return percent <= 50 ? Blue : percent <= 80 ? Blue.Mix(Violet, (percent.Value - 50) / 30)
            : Violet.Mix(Coral, (percent.Value - 80) / 15);
    }

    internal static GlowRgb BatteryColor(double? percent, bool current) =>
        percent is >= 0 and <= 100 ? LoadColor(Math.Clamp((30 - percent.Value) * 5, 0, 100), current) : Muted;

    internal static bool Animate(bool enabled, bool requested, bool overview, bool visible, bool active, bool systemAnimations, bool highContrast) =>
        enabled && requested && overview && visible && active && systemAnimations && !highContrast;
}
