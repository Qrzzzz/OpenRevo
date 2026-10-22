using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRevo.Core;

public enum SampleFreshness { Cached, Current, Stale, Disconnected }

public sealed record TopicSample(string Topic, JsonElement Payload, DateTimeOffset ReceivedAtUtc, bool Retained)
{
    [JsonIgnore] public long ReceivedTimestamp { get; init; } = TimeProvider.System.GetTimestamp();
    public TimeSpan Age(TimeProvider? clock = null) => (clock ?? TimeProvider.System).GetElapsedTime(ReceivedTimestamp);
    public SampleFreshness Freshness(TimeProvider clock, bool connected) => !connected ? SampleFreshness.Disconnected
        : Retained ? SampleFreshness.Cached
        : Age(clock) > TimeSpan.FromSeconds(15) ? SampleFreshness.Stale : SampleFreshness.Current;

    public string? Text(string field) => Payload.TryGetProperty(field, out var value) && value.ValueKind is not JsonValueKind.Null
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    public double? Number(string field) => double.TryParse(Text(field), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : null;
}

public sealed record Capability(string Name, string Field, bool? DeclaredSupported, string Availability, string Detail);

public static class DeviceAudit
{
    public static bool MatchesAuditedTelemetry(TopicSample sample) => !sample.Retained && AuditedProfile.MatchesCapabilities(sample);

    public static IReadOnlyList<Capability> Capabilities(TopicSample? sample, bool connected = true) => new[]
    {
        Make("系统监控", "SystemMonitorSupport", sample, "可读取", "CPU、GPU、风扇与电池状态"),
        Make("风扇与性能模式", "FanSettingsSupport", sample, "模式控制", "匹配已审计机型与关键配置且接通电源时，允许四模式切换；自定义使用原厂已保存配置，曲线未开放"),
        Make("狂暴模式", "TurboModeSupport", sample, "模式控制", "需实时能力和关键审计配置匹配，支持切换与显式恢复"),
        Make("屏幕色彩校准", "ColorCalibrationSupport", sample, "待审计", "非零值是能力编码；ICC、Gamma、矩阵恢复路径未核清"),
        Make("独显直连", "DGpuDirectConnectionSupport", sample, "待审计", "显示拓扑与重启条件未核清"),
        Make("功耗与超频", "OcSettingsSupport", sample, "待审计", "配置存在范围矛盾；不生成可写参数"),
        Make("RGB 灯带", "RGBLightbarSupport", sample, "待审计", "仅确认能力声明，尚未核清设备协议"),
        Make("液冷", "LiquidCoolingSupport", sample, "未开放", "本机能力声明为 0"),
        Make("Type-C 电源策略", "TypeCSupport", sample, "未开放", "本机能力声明为 0"),
        new Capability("电池充电阈值", "未找到可靠能力位", null, "待审计", "电量可读；充电上下限和恢复流程未验证")
    }.Select(capability => sample is not null && sample.Freshness(TimeProvider.System, connected) != SampleFreshness.Current
        ? capability with { Availability = connected ? "待实时确认" : "历史声明", Detail = capability.Detail + "；当前声明为缓存或历史数据" }
        : capability).ToArray();

    private static Capability Make(string name, string field, TopicSample? sample, string availability, string detail)
    {
        var value = sample?.Number(field);
        var encoded = field == "ColorCalibrationSupport";
        bool? supported = value is null ? null : value == 0 ? false : value == 1 || (encoded && value > 0 && value == Math.Truncate(value.Value)) ? true : null;
        return new(name, field, supported, supported is null ? "待确认" : supported == false ? "未声明支持" : availability,
            value is not null && supported is null ? detail + "；未知能力编码，未推断支持" : detail);
    }

    public static string ModeName(string? mode) => mode switch
    { "0" => "办公模式", "1" => "均衡模式", "2" => "狂暴模式", "3" => "自定义模式", null => "等待状态", _ => $"未知模式 ({mode})" };

    public static string? TgpInconsistency(TopicSample? sample)
    {
        var target = sample?.Number("GPU_ConfigurableTGPTarget");
        var maximum = sample?.Number("GPU_ConfigurableTGPMaximum");
        return target is not null && maximum is not null && target > maximum
            ? $"厂商返回的 TGP 目标值 {target:0.##} 高于同一响应的上限 {maximum:0.##}。这里只显示原始配置，未将其转换为可写范围。" : null;
    }
}
