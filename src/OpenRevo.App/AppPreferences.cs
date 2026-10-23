using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevo.Core;

namespace OpenRevo.App;

// UI preferences are independent of device identity, capabilities and mode journals.
internal sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public bool HasSize => Width > 0 && Height > 0;

    public static WindowBounds Fit(WindowBounds? saved, WindowBounds workArea, double scale)
    {
        var minimumWidth = Math.Min(workArea.Width, (int)(720 * scale));
        var minimumHeight = Math.Min(workArea.Height, (int)(440 * scale));
        var width = Math.Clamp(saved is { HasSize: true } ? saved.Width : Math.Min((int)(1180 * scale), workArea.Width - (int)(64 * scale)), minimumWidth, workArea.Width);
        var height = Math.Clamp(saved is { HasSize: true } ? saved.Height : Math.Min((int)(820 * scale), workArea.Height - (int)(64 * scale)), minimumHeight, workArea.Height);
        var overlaps = saved is { HasSize: true } && (long)saved.X + saved.Width > workArea.X
            && saved.X < (long)workArea.X + workArea.Width && (long)saved.Y + saved.Height > workArea.Y
            && saved.Y < (long)workArea.Y + workArea.Height;
        return new WindowBounds(
            overlaps ? Math.Clamp(saved!.X, workArea.X, workArea.X + workArea.Width - width) : workArea.X + (workArea.Width - width) / 2,
            overlaps ? Math.Clamp(saved!.Y, workArea.Y, workArea.Y + workArea.Height - height) : workArea.Y + (workArea.Height - height) / 2,
            width, height);
    }
}

internal enum AppThemePreference { System, Light, Dark }

internal sealed record AppPreferences(AppThemePreference Theme = AppThemePreference.System, WindowBounds? Window = null, bool Maximized = false,
    bool StayInTray = true, bool GlowEnabled = true, GlowDisplay GlowDisplay = GlowDisplay.Preset,
    GlowPalette GlowPalette = GlowPalette.Spectrum, bool GlowMotion = false)
{
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppPreferences Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), Options) ?? new();
            return settings with
            {
                Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppThemePreference.System,
                GlowDisplay = Enum.IsDefined(settings.GlowDisplay) ? settings.GlowDisplay : GlowDisplay.Preset,
                GlowPalette = Enum.IsDefined(settings.GlowPalette) ? settings.GlowPalette : GlowPalette.Spectrum,
                Window = settings.Window is { HasSize: true } ? settings.Window : null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }

    public void Save(string path) => DiagnosticFile.Write(path, JsonSerializer.Serialize(this, Options));
}
