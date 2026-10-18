using System.Text.Json;

namespace OpenRevo.Core;

public static class ProtocolEvidence
{
    public static JsonElement Registry { get; } = Load();
    private static JsonElement Load()
    {
        using var stream = typeof(ProtocolEvidence).Assembly.GetManifestResourceStream("OpenRevo.registry.json")
            ?? throw new InvalidDataException("Missing evidence registry.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unknown evidence schema.");
        var enabled = root.GetProperty("requests").EnumerateArray().Where(r => r.GetProperty("enabled").GetBoolean()).ToArray();
        if (enabled.Length != Enum.GetValues<ReadRequest>().Length) throw new InvalidDataException("Evidence allowlist drift.");
        foreach (var request in Enum.GetValues<ReadRequest>())
        {
            var row = enabled.Single(r => r.GetProperty("id").GetString() == request.ToString());
            var (topic, action) = ReadProtocol.Describe(request);
            if (row.GetProperty("topic").GetString() != topic || row.GetProperty("action").GetString() != action
                || row.GetProperty("qos").GetInt32() != 0 || row.GetProperty("retain").GetBoolean())
                throw new InvalidDataException("Evidence and runtime request contract disagree.");
        }
        if (!root.GetProperty("subscriptions").EnumerateArray().Select(r => r.GetProperty("topic").GetString())
                .SequenceEqual(ReadProtocol.Topics)) throw new InvalidDataException("Evidence subscription drift.");
        return root.Clone();
    }
}
