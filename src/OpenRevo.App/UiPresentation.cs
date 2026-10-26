using OpenRevo.Core;

namespace OpenRevo.App;

// Presentation contracts only: none of these helpers grant device authority.
internal static class UiPresentation
{
    internal static int MetricColumns(double width, double textScale) =>
        width >= 850 * Math.Max(1, textScale) ? 3 : width >= 500 * Math.Max(1, textScale) ? 2 : 1;
    internal static string MetricTime(SampleFreshness? freshness, TimeSpan age, bool available)
    {
        var time = MetricCardTheme.Timestamp(freshness, age);
        if (freshness is null) return "等待设备数据";
        if (freshness == SampleFreshness.Disconnected) return $"历史数据 · {time.Age}";
        if (freshness == SampleFreshness.Cached) return $"缓存数据 · {time.Age}";
        if (!available) return $"暂无有效读数 · {time.Age}";
        return freshness == SampleFreshness.Current ? $"更新于 {time.Age}" : $"上次更新 {time.Age} · 较旧";
    }

    internal static bool ChoiceEnabled(bool connected, bool busy, bool current, bool canSwitch, bool canSwitchTo) =>
        connected && !busy && (canSwitchTo || current && canSwitch);

    internal static (string State, string Reason) Capability(bool implemented, bool? declared, SampleFreshness? freshness,
        bool connected, bool ready, string blockedReason)
    {
        if (!implemented) return ("尚未提供", "本版尚未提供此项操作");
        if (!connected) return ("暂不可用", "设备未连接");
        if (freshness is null) return ("等待数据", "等待原厂能力声明");
        if (freshness != SampleFreshness.Current) return ("暂不可用", "等待最新原厂能力声明");
        if (declared != true) return ("暂不可用", "原厂未声明支持");
        return ready ? ("可用", "") : ("暂不可用", blockedReason);
    }

    internal static bool ShowModeFeedback(bool busy, PilotPhase phase, bool failed, bool ready) =>
        busy || failed || !ready || phase is PilotPhase.Applied or PilotPhase.Restored or PilotPhase.Uncertain or PilotPhase.Interrupted;

    internal static string AppliedMessage => "当前模式已生效，无需保存。可继续切换，或撤销最近一次切换。";
}

internal static class UiStyle
{
    internal const double PageTitle = 28;
    internal const double ModuleTitle = 18;
    internal const double Body = 14;
    internal const double Reading = 44;
    internal const double ModeReading = 40;
    internal const double CardRadius = 14;
    internal const double MetricRadius = 20;
    internal const double CardPadding = 18;
    internal const double SectionSpacing = 16;
}
