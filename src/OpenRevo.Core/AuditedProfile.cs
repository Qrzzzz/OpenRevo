using System.Text.Json;

namespace OpenRevo.Core;

public sealed record FileIdentity(string Sha256, long Bytes);
public sealed record MachineProfile(string Id, string Manufacturer, string Model, string Board, string Bios,
    FileIdentity Bridge, FileIdentity Service, IReadOnlyDictionary<string, string> CapabilityIdentity,
    IReadOnlyDictionary<string, string> TelemetryRequirements);

public static class AuditedProfile
{
    public static MachineProfile Current { get; } = Load();
    public static string BridgeHash => Current.Bridge.Sha256;
    public static string ServiceHash => Current.Service.Sha256;

    private static MachineProfile Load()
    {
        using var stream = typeof(AuditedProfile).Assembly.GetManifestResourceStream("OpenRevo.device-profiles.json")
            ?? throw new InvalidDataException("Missing embedded machine profiles.");
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || json.RootElement.GetProperty("profiles").GetArrayLength() != 1)
            throw new InvalidDataException("Unreviewed machine profile schema.");
        return json.RootElement.GetProperty("profiles")[0].Deserialize<MachineProfile>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid machine profile.");
    }

    public static void ValidateDevice(DeviceIdentity identity)
    {
        if (identity.Manufacturer != Current.Manufacturer || identity.Model != Current.Model || identity.Board != Current.Board || identity.Bios != Current.Bios)
            throw new MonitoringException(DiagnosticCode.UnsupportedDevice);
    }

    public static void ValidateFiles(string bridgeHash, string serviceHash)
    {
        if (bridgeHash != BridgeHash || serviceHash != ServiceHash)
            throw new MonitoringException(DiagnosticCode.ServiceVersionMismatch);
    }

    public static bool MatchesCapabilities(TopicSample sample) => sample.Topic == "Customize/SupportInfo"
        && Current.CapabilityIdentity.Concat(Current.TelemetryRequirements).All(p => sample.Text(p.Key) == p.Value);
}
