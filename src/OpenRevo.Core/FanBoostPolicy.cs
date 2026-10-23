using System.Text.Json;

namespace OpenRevo.Core;

public static class FanBoostPolicy
{
    public static JsonElement Evidence { get; } = Load();
    public static bool Enabled => Evidence.GetProperty("verified").GetBoolean();
    public static string Action(bool enabled) => Evidence.GetProperty("actions").GetProperty(enabled ? "on" : "off").GetString()!;
    private static JsonElement Load()
    {
        using var stream = typeof(FanBoostPolicy).Assembly.GetManifestResourceStream("OpenRevo.fan-boost.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("profileId").GetString() != AuditedProfile.Current.Id
            || root.GetProperty("actions").GetProperty("on").GetString() != "FAN_BOOST_ON"
            || root.GetProperty("actions").GetProperty("off").GetString() != "FAN_BOOST_OFF"
            || !root.GetProperty("acOnly").GetBoolean()) throw new InvalidDataException("Unreviewed fan control policy.");
        var evidence = root.GetProperty("evidence").EnumerateArray().ToArray();
        if (!evidence.Any(e => e.GetProperty("kind").GetString() == "vendor-ui")
            || evidence.Any(e => !e.GetProperty("configurationRestoredExactly").GetBoolean())
            || !evidence.Where(e => e.GetProperty("kind").GetString()!.StartsWith("independent", StringComparison.Ordinal))
                .SelectMany(e => e.GetProperty("boostedModes").EnumerateArray().Select(m => m.GetString())).Distinct().Order().SequenceEqual(new[] { "0", "1", "2", "3" }))
            throw new InvalidDataException("Incomplete fan control evidence.");
        return root.Clone();
    }
}
