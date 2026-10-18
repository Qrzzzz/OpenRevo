using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenRevo.Core;

public sealed record DeviceIdentity(string Manufacturer, string Model, string Board, string Bios);

public sealed class BridgeInstallation
{
    public required DeviceIdentity Device { get; init; }
    public required string BridgePath { get; init; }
    public required string BridgeSha256 { get; init; }
    public required string ServiceSha256 { get; init; }
    internal string Password { get; init; } = string.Empty;
}

[SupportedOSPlatform("windows")]
public static partial class AuditedInstallation
{
    public static BridgeInstallation Inspect()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var bios = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Read(string key) => Convert.ToString(bios?.GetValue(key)) ?? "未知";
        var identity = new DeviceIdentity(Read("SystemManufacturer"), Read("SystemProductName"), Read("BaseBoardProduct"), Read("BIOSVersion"));
        AuditedProfile.ValidateDevice(identity);
        using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge");
        var imagePath = Environment.ExpandEnvironmentVariables(Convert.ToString(service?.GetValue("ImagePath")) ?? "").Trim();
        if (imagePath.StartsWith('"') && imagePath.EndsWith('"')) imagePath = imagePath[1..^1];
        if (!Path.IsPathFullyQualified(imagePath) || imagePath.StartsWith(@"\\") || !imagePath.EndsWith(@"\AiStoneService\GCUBridge.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("未发现已审计的 GCUBridge 服务安装路径。" );
        var bridgePath = Path.GetFullPath(imagePath);
        var servicePath = Path.Combine(Path.GetDirectoryName(bridgePath)!, "MyControlCenter", "GCUService.exe");
        if (new FileInfo(bridgePath).Length != 2125496 || new FileInfo(servicePath).Length != 15822584)
            throw new InvalidOperationException("厂商服务文件大小与已审计版本不符，请重新审计版本。" );
        var bridge = File.ReadAllBytes(bridgePath);
        var bridgeHash = Convert.ToHexString(SHA256.HashData(bridge));
        using var stream = File.OpenRead(servicePath);
        var serviceHash = Convert.ToHexString(SHA256.HashData(stream));
        AuditedProfile.ValidateFiles(bridgeHash, serviceHash);
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 2; offset++)
        {
            var text = Encoding.Unicode.GetString(bridge, offset, (bridge.Length - offset) / 2 * 2);
            foreach (Match match in CredentialSuffix().Matches(text)) suffixes.Add(match.Value);
        }
        if (suffixes.Count != 1) throw new InvalidOperationException("无法识别本机桥接服务的认证格式。" );
        return new BridgeInstallation { Device = identity, BridgePath = bridgePath, BridgeSha256 = bridgeHash,
            ServiceSha256 = serviceHash, Password = "PluginClient" + suffixes.Single() + ReadProtocol.Slot };
    }

    [GeneratedRegex(@"_Pwd\d{6,24}_", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialSuffix();
}
