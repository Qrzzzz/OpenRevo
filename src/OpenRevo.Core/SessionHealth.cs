namespace OpenRevo.Core;

public sealed record SessionEvent(DateTimeOffset AtUtc, string Kind, string Detail);
public sealed record TopicHealth(string Topic, long Received, long LiveReceived, string Freshness,
    double? AgeSeconds, DateTimeOffset? LastReceivedAtUtc, DateTimeOffset? LastChangedAtUtc, double MaximumLiveGapSeconds);
public sealed record SessionHealthSnapshot(DateTimeOffset StartedAtUtc, DateTimeOffset? StoppedAtUtc,
    double DurationSeconds, long ReceivedMessages, long SentReadRequests, long SamplingRequests, DiagnosticCode? StopReason,
    IReadOnlyList<TopicHealth> Topics, IReadOnlyList<SessionEvent> Events, string SensorNotice);

/// <summary>Bounded session observations. Missing sensors are advisory, never transport authorization.</summary>
public sealed class SessionHealth(TimeProvider clock)
{
    private readonly object _gate = new();
    private sealed class TopicState
    {
        internal TopicSample? Last, LastLive;
        internal DateTimeOffset? ChangedAt;
        internal long Count, LiveCount;
        internal double MaxGap;
    }
    private readonly Dictionary<string, TopicState> _topics = ReadProtocol.Topics.ToDictionary(t => t, _ => new TopicState());
    private readonly Queue<SessionEvent> _events = new();
    private long _start = clock.GetTimestamp();
    private DateTimeOffset _startedAt = clock.GetUtcNow();
    private DateTimeOffset? _stoppedAt;
    private double? _duration;
    private long _messages, _requests, _sampling;
    private bool? _ac;
    private DiagnosticCode? _stopReason;
    public void RecordEvent(string kind, string detail)
    {
        lock (_gate)
        {
            _events.Enqueue(new(clock.GetUtcNow(), kind, detail));
            while (_events.Count > 64) _events.Dequeue();
        }
    }
    internal void Begin()
    {
        lock (_gate) { _start = clock.GetTimestamp(); _startedAt = clock.GetUtcNow(); RecordEvent("SessionStarted", "开始本机会话"); }
    }
    internal void Request(ReadRequest request)
    {
        lock (_gate)
        {
            _requests++;
            if (request == ReadRequest.EnableTelemetry) { _sampling++; RecordEvent("SamplingRequested", "已请求厂商共享采样"); }
        }
    }
    internal void Observe(TopicSample sample)
    {
        lock (_gate)
        {
            if (!_topics.TryGetValue(sample.Topic, out var state)) return;
            _messages++; state.Count++;
            if (state.Last is null || state.Last.Payload.GetRawText() != sample.Payload.GetRawText()) state.ChangedAt = sample.ReceivedAtUtc;
            state.Last = sample;
            if (!sample.Retained)
            {
                if (state.LastLive is not null) state.MaxGap = Math.Max(state.MaxGap, clock.GetElapsedTime(state.LastLive.ReceivedTimestamp, sample.ReceivedTimestamp).TotalSeconds);
                state.LastLive = sample; state.LiveCount++;
                if (sample.Topic == "Fan/Status" && bool.TryParse(sample.Text("IsAC"), out var ac) && _ac != ac)
                { _ac = ac; RecordEvent("VendorPowerSource", ac ? "厂商状态：交流电" : "厂商状态：电池"); }
            }
        }
    }
    internal void Stop(DiagnosticCode? reason)
    {
        lock (_gate)
        {
            if (_stoppedAt is not null) return;
            _stopReason = reason; _stoppedAt = clock.GetUtcNow(); _duration = clock.GetElapsedTime(_start).TotalSeconds;
            RecordEvent("Stopped", reason?.ToString() ?? "会话结束");
        }
    }
    public SessionHealthSnapshot Snapshot(bool connected)
    {
        lock (_gate)
        {
            var duration = _duration ?? clock.GetElapsedTime(_start).TotalSeconds;
            var topics = _topics.Select(p => new TopicHealth(p.Key, p.Value.Count, p.Value.LiveCount,
                p.Value.Last?.Freshness(clock, connected).ToString() ?? "Missing", p.Value.Last?.Age(clock).TotalSeconds,
                p.Value.Last?.ReceivedAtUtc, p.Value.ChangedAt, p.Value.MaxGap)).ToArray();
            var missing = topics.Where(t => t.Topic.StartsWith("System/", StringComparison.Ordinal) && t.LiveReceived == 0).Select(t => t.Topic).ToArray();
            var notice = connected && duration >= 30 && missing.Length > 0
                ? "尚未收到实时传感器：" + string.Join("、", missing.Select(t => t[7..])) + "。其他状态仍可使用；点击刷新可重新请求采样。"
                : connected && topics.Any(t => t.Topic.StartsWith("System/", StringComparison.Ordinal) && t.Freshness == "Stale")
                    ? "部分传感器较久未推送；显示的是最后回报值，不代表硬件故障。可点击刷新重新请求采样。" : "";
            return new(_startedAt, _stoppedAt, duration, _messages, _requests, _sampling, _stopReason, topics, _events.ToArray(), notice);
        }
    }
}
