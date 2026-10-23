using System.Text.Json;

namespace OpenRevo.Core;

public sealed record AuditDifference(string Category, string Field, string Expected, string Observed);
public sealed record ConfigurationReview(string? Mode, IReadOnlyList<AuditDifference> Differences);

public static class AuditReview
{
    // The only non-control Fan/Status fields excluded from configuration equality.
    // Unknown fields remain part of the comparison until classified by another audit.
    public static JsonElement Policy { get; } = Load();
    public static IReadOnlyList<string> AdvisoryFields { get; } = Array.AsReadOnly(Policy.GetProperty("advisoryFields").EnumerateArray().Select(v => v.GetString()!).ToArray());
    public static IReadOnlyList<string> IdentityFields { get; } = Array.AsReadOnly(Policy.GetProperty("platformIdentityFields").EnumerateArray().Select(v => v.GetString()!).ToArray());
    private static JsonElement Load()
    {
        using var stream = typeof(AuditReview).Assembly.GetManifestResourceStream("OpenRevo.configuration-policy.json")!;
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("profileId").GetString() != AuditedProfile.Current.Id
            || !root.GetProperty("advisoryFields").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "BSOD_DefaultNotify", "BSOD_TimestampRestored" })
            || !root.GetProperty("platformIdentityFields").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "IsAMDPlatform", "IsNvGpu" })
            || !root.GetProperty("liveStateFields").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "OperatingMode", "IsAC" })
            || root.GetProperty("unknownFields").GetString() != "require review") throw new InvalidDataException("Unreviewed configuration classification.");
        return root.Clone();
    }
    public static string Category(string field) => AdvisoryFields.Contains(field) ? "notification"
        : IdentityFields.Contains(field) ? "platform identity"
        : field is "OperatingMode" or "IsAC" ? "live state" : "control configuration";
    private static bool Compared(string field) => !AdvisoryFields.Contains(field) && field is not ("OperatingMode" or "IsAC");
    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "null" : value.ToString();

    public static IReadOnlyList<AuditDifference> ConfigurationDifferences(JsonElement expected, JsonElement actual)
    {
        var differences = new List<AuditDifference>();
        foreach (var property in expected.EnumerateObject().Where(p => Compared(p.Name)))
        {
            if (!actual.TryGetProperty(property.Name, out var value))
                differences.Add(new(Category(property.Name), property.Name, Value(property.Value), "missing"));
            else if (value.GetRawText() != property.Value.GetRawText())
                differences.Add(new(Category(property.Name), property.Name, Value(property.Value), Value(value)));
        }
        foreach (var property in actual.EnumerateObject().Where(p => Compared(p.Name) && !expected.TryGetProperty(p.Name, out _)))
            differences.Add(new("unclassified", property.Name, "not present in audit", Value(property.Value)));
        // Null is a known transient vendor representation; the configuration fingerprint resolves it.
        if (actual.TryGetProperty("OperatingMode", out var mode) && mode.ValueKind != JsonValueKind.Null
            && expected.TryGetProperty("OperatingMode", out var original) && original.ValueKind != JsonValueKind.Null
            && original.GetRawText() != mode.GetRawText())
            differences.Add(new("live state", "OperatingMode", Value(original), Value(mode)));
        return differences;
    }

    public static ConfigurationReview Configuration(TopicSample? sample)
    {
        if (sample is null) return new(null, [new("live state", "Fan/Status", "fresh status", "missing")]);
        if (!bool.TryParse(sample.Text("IsAC"), out _)) return new(null, [new("live state", "IsAC", "true / false", sample.Text("IsAC") ?? "missing")]);
        var configurations = ModePilotPolicy.Evidence.GetProperty("observedConfigurations").EnumerateObject().ToArray();
        var mode = sample.Text("OperatingMode");
        var candidates = configurations.Where(p => mode is null || p.Name == mode).ToArray();
        if (candidates.Length == 0) return new(null, [new("live state", "OperatingMode", "0 / 1 / 2 / 3", mode ?? "missing")]);
        // Fan boost is an independently verified control state. Exact baseline equality stays strict.
        var reviews = candidates.Select(p => new ConfigurationReview(p.Name, ConfigurationDifferences(p.Value, sample.Payload)
            .Where(d => d.Field != "FanBoostEnable" || sample.Text("FanBoostEnable") is not ("0" or "1")).ToArray())).ToArray();
        var matches = reviews.Where(r => r.Differences.Count == 0).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1) return new(null, [new("live state", "OperatingMode", "unique audited configuration", "ambiguous")]);
        var closest = reviews.MinBy(r => r.Differences.Count)!;
        return closest with { Mode = null };
    }

    public static IReadOnlyList<AuditDifference> Device(DeviceIdentity device)
    {
        var expected = AuditedProfile.Current;
        var result = new List<AuditDifference>();
        void Compare(string field, string required, string observed)
        { if (required != observed) result.Add(new("machine identity", field, required, observed)); }
        Compare("Manufacturer", expected.Manufacturer, device.Manufacturer);
        Compare("Model", expected.Model, device.Model);
        Compare("Board", expected.Board, device.Board);
        Compare("BIOS", expected.Bios, device.Bios);
        return result;
    }

    public static IReadOnlyList<AuditDifference> Capabilities(TopicSample? sample)
    {
        var profile = AuditedProfile.Current;
        return profile.CapabilityIdentity.Concat(profile.TelemetryRequirements)
            .Where(p => sample?.Text(p.Key) != p.Value)
            .Select(p => new AuditDifference(profile.CapabilityIdentity.ContainsKey(p.Key) ? "machine identity" : "monitoring requirement",
                p.Key, p.Value, sample?.Text(p.Key) ?? "missing")).ToArray();
    }

    public static IReadOnlyList<AuditDifference> Files(string bridgeHash, long bridgeBytes, string serviceHash, long serviceBytes)
    {
        var result = new List<AuditDifference>();
        void Compare(string name, FileIdentity required, string hash, long bytes)
        {
            if (required.Sha256 != hash) result.Add(new("service version", name + ".SHA256", required.Sha256, hash));
            if (required.Bytes != bytes) result.Add(new("service version", name + ".Bytes", required.Bytes.ToString(), bytes.ToString()));
        }
        Compare("GCUBridge", AuditedProfile.Current.Bridge, bridgeHash, bridgeBytes);
        Compare("GCUService", AuditedProfile.Current.Service, serviceHash, serviceBytes);
        return result;
    }
}
