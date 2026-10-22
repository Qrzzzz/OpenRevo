using System.Text.Json;

namespace OpenRevo.Core;

public static class PreviousModeJournal
{
    public static string Describe(string json)
    {
        if (json.Length > 65536) return "旧模式记录过大，未读取；请核对当前模式。";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Phase", out var phase))
                return "旧模式记录缺少状态；请核对当前模式。";
            return phase.GetString() switch
            {
                "Kept" => "上次已保留当前模式。旧记录仅供查看，不会重放。",
                "Restored" or "Idle" => "",
                "Applied" => "上次切换已回读确认，但未选择保留或恢复。请核对当前模式；旧基线不会跨连接恢复。",
                _ => "上次模式操作未确认结束，请核对原厂当前状态；旧记录不会重放。"
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return "旧模式记录无法解析，请核对当前模式；不会重放旧操作。"; }
    }
}
