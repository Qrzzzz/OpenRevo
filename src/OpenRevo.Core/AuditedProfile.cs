namespace OpenRevo.Core;

public static class AuditedProfile
{
    public const string BridgeHash = "7A1405E231A5E8581C4E9FC4FCE2302A77578742AC643AA3D42F7DADACA80695";
    public const string ServiceHash = "0469CF6D9F55B9C9ABED70DB3A11AD126F5BAD2E9246F45FB747E17E11AED450";

    public static void ValidateDevice(DeviceIdentity identity)
    {
        if (identity.Manufacturer != "MECHREVO" || identity.Board != "YAOSHI Series-X6AR55xY" || identity.Bios != "N.1.18MRO24")
            throw new InvalidOperationException("当前主板或 BIOS 尚未列入本机审计配置。未启用连接，请重新审计机型。" );
    }

    public static void ValidateFiles(string bridgeHash, string serviceHash)
    {
        if (bridgeHash != BridgeHash || serviceHash != ServiceHash)
            throw new InvalidOperationException("厂商服务版本与已审计文件不一致。为避免误用协议，本次不连接；请重新审计更新后的版本。" );
    }
}
