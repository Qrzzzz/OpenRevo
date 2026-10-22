using System.Text.Json;
using System.Security.Cryptography;

namespace OpenRevo.Core;

public enum PilotMode { Office = 0, Balanced = 1, Turbo = 2, Custom = 3 }
public enum PilotPhase { Idle, Preparing, Sending, AwaitingReadback, Applied, Restoring, Restored, Uncertain, Interrupted, Kept }
public sealed record ModePilotResult(bool Succeeded, string Detail, TopicSample? Snapshot);
public sealed record ModeJournal(string Id, string ProfileId, PilotPhase Phase, DateTimeOffset UpdatedAtUtc,
    TopicSample? Baseline, PilotMode? Target, TopicSample? Confirmed, string Detail,
    string? BaselineMode = null, string? ConfirmedMode = null, string? ModeSource = null, string? FailureReason = null);

public static class ModePilotPolicy
{
    public static readonly string[] ConfigurationFields = ["OperatingMode", "ProfileName", "OfficeProfileIndex", "GamingProfileIndex",
        "FAN_TableName", "FanBoostEnable", "CPU_PL1", "CPU_PL2", "CPU_PL4", "CPU_TccOffset", "CPU_OffsetCoreVoltage",
        "GPU_ConfigurableTGPTarget", "GPU_DynamicBoost", "GPU_CoreClockOffset", "GPU_MemoryClockOffset", "OverClockingSwitch", "PowerMode", "Extreme", "Silent"];
    public static JsonElement Evidence { get; } = Load();
    public static string Action(PilotMode mode) => mode switch
    {
        PilotMode.Office => "OPERATING_OFFICE_MODE", PilotMode.Balanced => "OPERATING_GAMING_MODE",
        PilotMode.Turbo => "OPERATING_TURBO_MODE", PilotMode.Custom => "OPERATING_CUSTOM_MODE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static bool Allows(PilotMode mode) => Enum.IsDefined(mode) && Enabled && Evidence.GetProperty("observedConfigurations").TryGetProperty(((int)mode).ToString(), out _);
    public static bool Enabled => Evidence.GetProperty("verified").GetBoolean();
    private static JsonElement Load()
    {
        using var stream = typeof(ModePilotPolicy).Assembly.GetManifestResourceStream("OpenRevo.mode-pilot.json")!;
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("profileId").GetString() != AuditedProfile.Current.Id
            || root.GetProperty("actions").GetProperty("0").GetString() != "OPERATING_OFFICE_MODE"
            || root.GetProperty("actions").GetProperty("1").GetString() != "OPERATING_GAMING_MODE"
            || root.GetProperty("actions").GetProperty("2").GetString() != "OPERATING_TURBO_MODE"
            || root.GetProperty("actions").GetProperty("3").GetString() != "OPERATING_CUSTOM_MODE"
            || !root.GetProperty("acOnly").GetBoolean()) throw new InvalidDataException("Unreviewed pilot policy.");
        if (root.GetProperty("verified").GetBoolean())
        {
            using var evidence = typeof(ModePilotPolicy).Assembly.GetManifestResourceStream("OpenRevo.mode-evidence.json")!;
            var hash = Convert.ToHexString(SHA256.HashData(evidence)).ToLowerInvariant();
            if (hash != root.GetProperty("evidence").GetProperty("sha256").GetString()) throw new InvalidDataException("Pilot evidence identity mismatch.");
        }
        var classification = AuditReview.Policy;
        var fields = new[] { "platformIdentityFields", "liveStateFields", "advisoryFields", "controlConfigurationFields" }
            .SelectMany(group => classification.GetProperty(group).EnumerateArray().Select(v => v.GetString()!)).ToArray();
        if (fields.Distinct().Count() != fields.Length || root.GetProperty("observedConfigurations").EnumerateObject()
            .Any(mode => !new HashSet<string>(fields).SetEquals(mode.Value.EnumerateObject().Select(p => p.Name))))
            throw new InvalidDataException("Configuration classification does not cover the audited field set.");
        return root.Clone();
    }
    // This vendor can temporarily return null OperatingMode while switching.
    // Keep that raw value; resolve mode separately from corroborating evidence.
    public static bool Complete(TopicSample? sample) => sample is not null && ConfigurationFields.Where(f => f != "OperatingMode").All(f => sample.Text(f) is not null);
    public static bool SameConfiguration(TopicSample? a, TopicSample? b) => Complete(a) && Complete(b)
        && AuditReview.ConfigurationDifferences(a!.Payload, b!.Payload).Count == 0;
    internal static bool Audited(TopicSample sample)
    {
        return AuditedMode(sample) is not null;
    }
    internal static string? AuditedMode(TopicSample sample) => !Enabled || !Complete(sample) ? null : AuditReview.Configuration(sample).Mode;
}

/// <summary>Daily mode switching with a baseline for the latest operation in the same connection.</summary>
public sealed class ModePilot
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly bool _audit;
    private readonly TimeSpan _timeout;
    private Func<PilotMode, CancellationToken, Task>? _send;
    private Func<CancellationToken, Task>? _query;
    private Func<bool>? _authorized;
    private CancellationToken _connection;
    private TopicSample? _capabilities, _status, _tray;
    private TopicSample? _expectedBefore;
    private PilotMode _pendingTarget;
    private bool? _systemAc;
    private long _capSequence, _statusSequence;
    private int _busy;
    private Action<ModeJournal>? _save;
    private ModeJournal _journal = new(Guid.NewGuid().ToString("N"), AuditedProfile.Current.Id, PilotPhase.Idle, DateTimeOffset.UtcNow, null, null, null, "可切换四种模式，需接通电源；自定义调用原厂已保存配置");
    internal ModePilot(TimeProvider clock, bool audit = false, TimeSpan? timeout = null) { _clock = clock; _audit = audit; _timeout = timeout ?? TimeSpan.FromSeconds(10); }
    public ModeJournal Journal { get { lock (_gate) return _journal; } }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public bool Enabled => ModePilotPolicy.Enabled || _audit;
    public TopicSample? CurrentStatus { get { lock (_gate) return _status; } }
    public string? CurrentMode { get { lock (_gate) return _status is null ? null : ResolveMode(_status); } }
    public ConfigurationReview ConfigurationReview { get { lock (_gate) return AuditReview.Configuration(_status); } }
    public string ControlStatus
    {
        get
        {
            lock (_gate)
            {
                if (Busy) return "正在操作，请等待回读。";
                if (_journal.Phase is PilotPhase.Uncertain or PilotPhase.Interrupted)
                    return "本次操作已停止；核对原厂当前模式后手动重新连接，不会重放旧操作。";
                if (_status is null) return "等待实时模式配置。";
                try { Require(_status); return "身份、能力和关键配置匹配；可切换到其他模式。"; }
                catch (InvalidOperationException ex) { return ex.Message; }
            }
        }
    }
    public bool CanSwitch => Ready(false);
    public bool CanSwitchTo(PilotMode mode)
    {
        lock (_gate) return (_audit || ModePilotPolicy.Allows(mode)) && Ready(false) && CurrentMode != ((int)mode).ToString()
            && (mode != PilotMode.Turbo || _capabilities?.Text("TurboModeSupport") == "1");
    }
    public bool CanRestore => Ready(true);
    public bool CanKeep => CanRestore;
    public ModePilotResult KeepCurrent()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(false, "已有操作进行中。", null);
        try
        {
            lock (_gate)
            {
                if (_journal.Phase != PilotPhase.Applied || _status is null)
                    return new(false, "没有可保留的已确认切换。", null);
                Require(_status);
                if (!ModePilotPolicy.SameConfiguration(_status, _journal.Confirmed))
                    throw new InvalidOperationException("当前配置已改变，请核对原厂状态。");
                Set(PilotPhase.Kept, "已保留当前模式，不发送命令；本次恢复基线已结束。");
                return new(true, _journal.Detail, _status);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            lock (_gate) Set(PilotPhase.Uncertain, "保留未完成，请核对当前状态及本地日志。", persistRequired: false);
            return new(false, Journal.Detail, null);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
    private bool Ready(bool restore)
    {
        lock (_gate)
        {
            if (Busy || _status is null || (restore ? _journal.Phase != PilotPhase.Applied : _journal.Phase is not (PilotPhase.Idle or PilotPhase.Restored or PilotPhase.Kept or PilotPhase.Applied))) return false;
            try { Require(_status); return !restore || ModePilotPolicy.SameConfiguration(_status, _journal.Confirmed); }
            catch (InvalidOperationException) { return false; }
        }
    }
    public void UpdatePowerSource(bool ac)
    {
        lock (_gate)
        {
            _systemAc = ac;
            if (!ac && _journal.Phase == PilotPhase.Applied)
                Set(PilotPhase.Uncertain, "已切换到电池供电；停止恢复写入，请核对原厂当前配置。", persistRequired: false);
        }
    }
    public void SetJournalWriter(Action<ModeJournal> writer) { lock (_gate) _save = writer; }

    internal void Bind(Func<PilotMode, CancellationToken, Task> send, Func<CancellationToken, Task> query, Func<bool> authorized, CancellationToken connection)
    { lock (_gate) { _send = send; _query = query; _authorized = authorized; _connection = connection; } }

    internal void Observe(TopicSample sample)
    {
        lock (_gate)
        {
            if (sample.Topic == "Customize/SupportInfo" && !sample.Retained) { _capabilities = sample; _capSequence++; }
            if (sample.Topic == "Tray/Status" && !sample.Retained)
            {
                _tray = sample;
                if (_journal.Phase == PilotPhase.Applied && (sample.Text("OperatingMode") != _journal.ConfirmedMode || !Ac(sample)))
                    Set(PilotPhase.Uncertain, "托盘状态已改变，停止恢复写入，请在原厂界面核对。", persistRequired: false);
            }
            if (sample.Topic != "Fan/Status" || sample.Retained) return;
            _status = sample; _statusSequence++;
            if (_journal.Phase == PilotPhase.Applied && (!ModePilotPolicy.SameConfiguration(sample, _journal.Confirmed) || !Ac(sample)))
                Set(PilotPhase.Uncertain, "配置或供电已改变，可能存在原厂干预；停止恢复写入，请在原厂界面核对。", persistRequired: false);
        }
    }

    internal void End()
    {
        lock (_gate)
        {
            _send = null; _query = null; _authorized = null;
            if (_journal.Phase is PilotPhase.Applied or PilotPhase.Sending or PilotPhase.AwaitingReadback or PilotPhase.Restoring)
                Set(PilotPhase.Interrupted, "连接已结束，不会自动恢复或重放；请用原厂界面核对基线。", persistRequired: false);
        }
    }

    private static bool Ac(TopicSample? sample) => bool.TryParse(sample?.Text("IsAC"), out var value) && value;
    private string? ResolveMode(TopicSample sample)
    {
        if (sample.Text("OperatingMode") is "0" or "1" or "2" or "3") return sample.Text("OperatingMode");
        if (sample.Text("OperatingMode") is not null) return null;
        var audited = ModePilotPolicy.AuditedMode(sample);
        if (audited is not null) return audited;
        if (_audit && _tray?.Freshness(_clock, true) == SampleFreshness.Current && Ac(_tray)
            && _tray.Text("OperatingMode") is "0" or "1" or "2" or "3") return _tray.Text("OperatingMode");
        return null;
    }
    internal void CheckSending()
    {
        lock (_gate)
        {
            if (_journal.Phase is not (PilotPhase.Sending or PilotPhase.Restoring) || _status is null)
                throw new InvalidOperationException("操作已中断。");
            Require(_status);
            RequireTarget(_pendingTarget);
            if (!ModePilotPolicy.SameConfiguration(_status, _expectedBefore)) throw new InvalidOperationException("发送前配置已改变，未发送。");
        }
    }
    private Task Query(CancellationToken token)
    {
        Func<CancellationToken, Task> query;
        lock (_gate) query = _query ?? throw new InvalidOperationException("连接未就绪。");
        return query(token);
    }
    private void RequireTarget(PilotMode mode)
    {
        if (mode == PilotMode.Turbo && _capabilities?.Text("TurboModeSupport") != "1")
            throw new InvalidOperationException("当前实时能力不允许狂暴模式，未发送。");
    }
    private void Require(TopicSample sample)
    {
        if (!Enabled || _send is null || _connection.IsCancellationRequested)
            throw new InvalidOperationException("试点尚未通过本机审计或连接已失效。");
        if (_capabilities is null || _capabilities.Freshness(_clock, true) != SampleFreshness.Current)
            throw new InvalidOperationException("实时能力未收到或已过期，请刷新后等待新响应。");
        if (!DeviceAudit.MatchesAuditedTelemetry(_capabilities))
            throw new InvalidOperationException("能力响应中的机型身份或监控就绪条件不匹配，请查看硬件能力与审计材料。");
        if (_capabilities.Text("FanSettingsSupport") != "1")
            throw new InvalidOperationException("当前 FanSettingsSupport 未声明模式控制支持；监测仍可继续。");
        if (_systemAc == false || !Ac(sample)) throw new InvalidOperationException("当前为电池供电或供电尚未确认，模式控制需接通电源。");
        if (sample.Freshness(_clock, true) != SampleFreshness.Current)
            throw new InvalidOperationException("模式配置已过期，请刷新并等待实时响应。");
        if (!ModePilotPolicy.Complete(sample) || ResolveMode(sample) is null)
            throw new InvalidOperationException("关键配置缺失或无法识别模式，请查看具体差异并保存审计材料。");
        if (!_audit && !ModePilotPolicy.Audited(sample)) throw new InvalidOperationException("关键配置与已审计结果不一致，请核对原厂配置并保存审计材料。");
        if (_tray?.Freshness(_clock, true) == SampleFreshness.Current && _tray.Text("OperatingMode") != ResolveMode(sample))
            throw new InvalidOperationException("两种状态的模式尚未一致，暂不允许发送。");
        if (_authorized?.Invoke() != true) throw new InvalidOperationException("监控授权已失效。");
        if (_save is null) throw new InvalidOperationException("恢复记录尚未配置，禁止发送。");
    }

    private void Set(PilotPhase phase, string detail, bool persistRequired = true)
    {
        _journal = _journal with { Phase = phase, Detail = detail, UpdatedAtUtc = _clock.GetUtcNow() };
        try { _save?.Invoke(_journal); }
        catch (Exception ex) when (!persistRequired && ex is IOException or UnauthorizedAccessException)
        { _journal = _journal with { Phase = PilotPhase.Uncertain, Detail = "恢复记录保存失败；禁止继续写入，请使用原厂界面。" }; }
    }

    private async Task<TopicSample> Fresh(CancellationToken token)
    {
        long cap, status;
        lock (_gate) { cap = _capSequence; status = _statusSequence; }
        await Query(token);
        var start = _clock.GetTimestamp();
        while (_clock.GetElapsedTime(start) < _timeout)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_capSequence > cap && _statusSequence > status && _status is not null) { Require(_status); return _status; }
            }
            await Task.Delay(75, token);
        }
        throw new TimeoutException("未收到新的完整能力和配置响应，未发送模式命令。");
    }

    public Task<ModePilotResult> SwitchAsync(PilotMode mode, CancellationToken token = default) => Operate(mode, false, token);
    public Task<ModePilotResult> RestoreAsync(CancellationToken token = default) => Operate(default, true, token);

    private async Task<ModePilotResult> Operate(PilotMode target, bool restore, CancellationToken caller)
    {
        if (!restore && !_audit && !ModePilotPolicy.Allows(target)) return new(false, "目标模式尚未完成本机回读与恢复审计，未发送。", null);
        if (!Enum.IsDefined(target)) return new(false, "只允许办公、均衡、狂暴或原厂已保存的自定义模式。", null);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(false, "已有操作进行中，未排队或发送。", null);
        var mayHaveSent = false;
        ModeJournal previous = Journal;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _connection);
            var token = linked.Token;
            lock (_gate)
            {
                previous = _journal;
                if (restore ? _journal.Phase != PilotPhase.Applied : _journal.Phase is not (PilotPhase.Idle or PilotPhase.Restored or PilotPhase.Kept or PilotPhase.Applied))
                    return new(false, "当前操作不可用；结果不确定时请核对原厂状态。", null);
                if (!restore) Set(PilotPhase.Preparing, "正在回读当前配置，尚未发送。");
            }
            var before = await Fresh(token);
            if (!restore && previous.Phase == PilotPhase.Applied && !ModePilotPolicy.SameConfiguration(before, previous.Confirmed))
                throw new InvalidOperationException("上次切换后配置已改变，停止继续切换。");
            TopicSample? baseline;
            lock (_gate)
            {
                if (restore)
                {
                    baseline = _journal.Baseline ?? throw new InvalidOperationException("缺少基线。");
                    if (ModePilotPolicy.SameConfiguration(before, baseline)) { Set(PilotPhase.Restored, "配置已回到基线，未重复发送。"); return new(true, _journal.Detail, before); }
                    if (!ModePilotPolicy.SameConfiguration(before, _journal.Confirmed)) throw new InvalidOperationException("配置已被改变，禁止覆盖其他控制者的结果。");
                    target = (PilotMode)int.Parse(_journal.BaselineMode ?? throw new InvalidOperationException("缺少已确认的基线模式。"));
                    if (!_audit && !ModePilotPolicy.Allows(target)) throw new InvalidOperationException("基线模式未经审计，禁止恢复写入。");
                }
                else
                {
                    baseline = before;
                    if (ResolveMode(before) == ((int)target).ToString())
                    {
                        _journal = previous;
                        Set(previous.Phase, previous.Detail);
                        return new(true, "已处于所选模式，未发送命令；现有恢复记录保持不变。", before);
                    }
                    _journal = new(Guid.NewGuid().ToString("N"), AuditedProfile.Current.Id, PilotPhase.Preparing, _clock.GetUtcNow(), baseline, target, null, "基线已保存", ResolveMode(before));
                }
                Require(before);
                RequireTarget(target);
                _pendingTarget = target;
                _expectedBefore = before;
                Set(restore ? PilotPhase.Restoring : PilotPhase.Sending, restore ? "恢复原模式；只发送一次。" : "切换模式；只发送一次。");
            }
            long sequence;
            Func<PilotMode, CancellationToken, Task> sender;
            lock (_gate) { sequence = _statusSequence; sender = _send ?? throw new InvalidOperationException("连接已结束。"); mayHaveSent = true; }
            await sender(target, token);
            lock (_gate) Set(PilotPhase.AwaitingReadback, "已发送，等待两次一致的新配置回读；尚未判定成功。");
            var start = _clock.GetTimestamp(); var lastQuery = start;
            TopicSample? first = null;
            long firstSequence = sequence;
            await Query(token);
            while (_clock.GetElapsedTime(start) < _timeout)
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_systemAc == false) throw new InvalidOperationException("已切换到电池供电，停止等待并交由原厂界面核对。");
                    if (_statusSequence > sequence && _status is not null)
                    {
                        if (!Ac(_status)) throw new InvalidOperationException("供电已改变，停止等待并交由原厂界面核对。");
                        if (_authorized?.Invoke() != true || _capabilities?.Freshness(_clock, true) != SampleFreshness.Current
                            || !DeviceAudit.MatchesAuditedTelemetry(_capabilities!)) throw new InvalidOperationException("能力或连接已失效。");
                        if (ModePilotPolicy.Complete(_status) && ResolveMode(_status) == ((int)target).ToString())
                        {
                            if (first is null) { first = _status; firstSequence = _statusSequence; }
                            else if (!ModePilotPolicy.SameConfiguration(first, _status)) { first = _status; firstSequence = _statusSequence; }
                            else if (_statusSequence > firstSequence && _clock.GetElapsedTime(first.ReceivedTimestamp, _status.ReceivedTimestamp) >= TimeSpan.FromMilliseconds(500))
                            {
                                Require(_status);
                                if (restore && !ModePilotPolicy.SameConfiguration(baseline, _status)) throw new InvalidOperationException("模式已恢复，但联动配置未回到基线。");
                                _journal = _journal with { Confirmed = _status, ConfirmedMode = ((int)target).ToString(),
                                    ModeSource = _status.Text("OperatingMode") is not null ? "Fan/Status" : _audit ? "fresh Tray/Status + Fan/Status configuration" : "audited exact configuration fingerprint" };
                                Set(restore ? PilotPhase.Restored : PilotPhase.Applied, restore ? "原模式和审计配置字段已回读恢复。" : "目标模式已两次回读一致。可继续切换、保留当前模式或恢复最近一次基线。");
                                return new(true, _journal.Detail, _status);
                            }
                        }
                        sequence = _statusSequence;
                    }
                }
                if (_clock.GetElapsedTime(lastQuery) >= TimeSpan.FromSeconds(1)) { await Query(token); lastQuery = _clock.GetTimestamp(); }
                await Task.Delay(75, token);
            }
            throw new TimeoutException("模式回读超时，结果不确定；不重发或自动恢复。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            lock (_gate)
            {
                _journal = _journal with { FailureReason = ex is InvalidOperationException or TimeoutException ? ex.Message : ex.GetType().Name };
                if (!mayHaveSent && previous.Phase == PilotPhase.Applied) _journal = previous with { FailureReason = _journal.FailureReason };
                Set(mayHaveSent || restore || previous.Phase == PilotPhase.Applied ? PilotPhase.Uncertain : PilotPhase.Idle,
                    mayHaveSent ? "结果不确定；请用原厂界面核对基线，不会自动重发或恢复。" : ex.Message, persistRequired: false);
            }
            return new(false, Journal.Detail, null);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
