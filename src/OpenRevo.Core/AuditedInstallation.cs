using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenRevo.Core;

public sealed record DeviceIdentity(string Manufacturer, string Model, string Board, string Bios);
public sealed record InstallationReport(DeviceIdentity Device, string ProfileId, string? BridgeSha256, string? ServiceSha256,
    string DeviceMatch, string FileMatch);

public sealed class BridgeInstallation
{
    public required DeviceIdentity Device { get; init; }
    public required string BridgePath { get; init; }
    public required string BridgeSha256 { get; init; }
    public required string ServiceSha256 { get; init; }
    public string ProfileId => AuditedProfile.Current.Id;
    internal string Password { get; init; } = string.Empty;
}

[SupportedOSPlatform("windows")]
public static partial class AuditedInstallation
{
    public static BridgeInstallation Inspect(Action<InstallationReport>? onInspection = null)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var bios = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Read(string key) => Convert.ToString(bios?.GetValue(key)) ?? "未知";
        var identity = new DeviceIdentity(Read("SystemManufacturer"), Read("SystemProductName"), Read("BaseBoardProduct"), Read("BIOSVersion"));
        var report = new InstallationReport(identity, AuditedProfile.Current.Id, null, null, "pending", "not checked");
        try { AuditedProfile.ValidateDevice(identity); }
        catch (MonitoringException) { onInspection?.Invoke(report with { DeviceMatch = "mismatch" }); throw; }
        report = report with { DeviceMatch = "matched" };
        onInspection?.Invoke(report);
        using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge");
        var imagePath = Environment.ExpandEnvironmentVariables(Convert.ToString(service?.GetValue("ImagePath")) ?? "").Trim();
        if (imagePath.StartsWith('"') && imagePath.EndsWith('"')) imagePath = imagePath[1..^1];
        if (!Path.IsPathFullyQualified(imagePath) || imagePath.StartsWith(@"\\") || !imagePath.EndsWith(@"\AiStoneService\GCUBridge.exe", StringComparison.OrdinalIgnoreCase))
            throw new MonitoringException(DiagnosticCode.InstallationNotFound);
        var bridgePath = Path.GetFullPath(imagePath);
        var servicePath = Path.Combine(Path.GetDirectoryName(bridgePath)!, "MyControlCenter", "GCUService.exe");
        if (!File.Exists(bridgePath) || !File.Exists(servicePath)) throw new MonitoringException(DiagnosticCode.InstallationNotFound);
        if (new FileInfo(bridgePath).Length != AuditedProfile.Current.Bridge.Bytes || new FileInfo(servicePath).Length != AuditedProfile.Current.Service.Bytes)
        {
            onInspection?.Invoke(report with { FileMatch = "size mismatch" });
            throw new MonitoringException(DiagnosticCode.ServiceVersionMismatch);
        }
        var bridge = File.ReadAllBytes(bridgePath);
        var bridgeHash = Convert.ToHexString(SHA256.HashData(bridge));
        using var stream = File.OpenRead(servicePath);
        var serviceHash = Convert.ToHexString(SHA256.HashData(stream));
        report = report with { BridgeSha256 = bridgeHash, ServiceSha256 = serviceHash };
        try { AuditedProfile.ValidateFiles(bridgeHash, serviceHash); }
        catch (MonitoringException) { onInspection?.Invoke(report with { FileMatch = "hash mismatch" }); throw; }
        onInspection?.Invoke(report with { FileMatch = "matched" });
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 2; offset++)
        {
            var text = Encoding.Unicode.GetString(bridge, offset, (bridge.Length - offset) / 2 * 2);
            foreach (Match match in CredentialSuffix().Matches(text)) suffixes.Add(match.Value);
        }
        if (suffixes.Count != 1) throw new MonitoringException(DiagnosticCode.CredentialFormat);
        return new BridgeInstallation { Device = identity, BridgePath = bridgePath, BridgeSha256 = bridgeHash,
            ServiceSha256 = serviceHash, Password = "PluginClient" + suffixes.Single() + ReadProtocol.Slot };
    }

    [GeneratedRegex(@"_Pwd\d{6,24}_", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialSuffix();
}
