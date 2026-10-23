namespace OpenRevo.Core;

public sealed record FanBoostJournal(string Phase, DateTimeOffset UpdatedAtUtc, bool? Target, TopicSample? Baseline, TopicSample? Confirmed, string Detail);

public sealed partial class ModePilot
{
    private Func<bool, CancellationToken, Task>? _sendFan;
    private Action<FanBoostJournal>? _saveFan;
    private bool _fanUncertain;
    private int _fanBusy;
    public bool FanBusy => Volatile.Read(ref _fanBusy) != 0;
    private TopicSample? _fanBefore;
    private FanBoostJournal _fanJournal = new("Idle", DateTimeOffset.UtcNow, null, null, null, "等待实时风扇状态");
    public FanBoostJournal FanJournal { get { lock (_gate) return _fanJournal; } }
    public bool? FanBoostEnabled { get { lock (_gate) return _status?.Text("FanBoostEnable") switch { "0" => false, "1" => true, _ => null }; } }
    public bool CanControlFan
    {
        get { lock (_gate) { if (Busy) return false; try { RequireFan(); return true; } catch (InvalidOperationException) { return false; } } }
    }
    public string FanControlStatus
    {
        get { lock (_gate) { if (Busy) return "正在操作，请等待回读。"; try { RequireFan(); return FanBoostEnabled == true ? "一键强冷已开启" : "自动调速"; } catch (InvalidOperationException ex) { return ex.Message; } } }
    }
    public void SetFanJournalWriter(Action<FanBoostJournal> writer) { lock (_gate) _saveFan = writer; }
    internal void BindFan(Func<bool, CancellationToken, Task> send) { lock (_gate) _sendFan = send; }
    private void RequireFan()
    {
        if (_status is null) throw new InvalidOperationException("等待实时风扇状态。");
        if (!FanBoostPolicy.Enabled) throw new InvalidOperationException("此版本尚未开放一键强冷。");
        Require(_status);
        if (_sendFan is null || _saveFan is null) throw new InvalidOperationException("风扇控制尚未就绪。");
        if (_capabilities?.Text("FanBoostBtnSupport") != "1" || !bool.TryParse(_status.Text("FanBoostBtnSupport"), out var supported) || !supported)
            throw new InvalidOperationException("当前设备未提供一键强冷功能。");
        if (FanBoostEnabled is null) throw new InvalidOperationException("无法识别一键强冷状态，请刷新数据。");
    }
    internal void CheckFanSending()
    {
        lock (_gate)
        {
            RequireFan();
            if (_fanJournal.Phase != "Sending" || !ModePilotPolicy.SameConfiguration(_status, _fanBefore))
                throw new InvalidOperationException("发送前风扇或模式配置已改变，未发送。");
        }
    }
    private void FanRecord(string phase, string detail)
    {
        _fanJournal = _fanJournal with { Phase = phase, Detail = detail, UpdatedAtUtc = _clock.GetUtcNow() };
        _saveFan?.Invoke(_fanJournal);
    }

    public async Task<ModePilotResult> SetFanBoostAsync(bool enabled, CancellationToken caller = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(false, "已有操作进行中，未排队或发送。", null);
        Interlocked.Exchange(ref _fanBusy, 1);
        var sent = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _connection);
            var token = linked.Token;
            lock (_gate) RequireFan();
            var before = await Fresh(token);
            long sequence;
            Func<bool, CancellationToken, Task> sender;
            lock (_gate)
            {
                RequireFan();
                if (FanBoostEnabled == enabled) return new(true, enabled ? "一键强冷已经开启，未重复发送。" : "当前已是自动调速，未重复发送。", before);
                _fanJournal = new("Preparing", _clock.GetUtcNow(), enabled, before, null, "正在准备风扇操作");
                if (_journal.Phase == PilotPhase.Applied) Set(PilotPhase.Kept, "当前模式已保留；风扇操作开始，结束此前的模式撤销记录。");
                _fanBefore = before;
                FanRecord("Sending", enabled ? "开启一键强冷；只发送一次。" : "关闭一键强冷，恢复原厂自动调速；只发送一次。");
                sequence = _statusSequence;
                sender = _sendFan!;
            }
            sent = true;
            await sender(enabled, token);
            lock (_gate) FanRecord("AwaitingReadback", "命令已发送，正在核对风扇状态。");
            var start = _clock.GetTimestamp(); var lastQuery = start;
            TopicSample? first = null; long firstSequence = sequence;
            await Query(token);
            while (_clock.GetElapsedTime(start) < _timeout)
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    RequireFan();
                    if (_statusSequence > sequence)
                    {
                        var after = _status!;
                        var changes = AuditReview.ConfigurationDifferences(before.Payload, after.Payload).Where(d => d.Field != "FanBoostEnable").ToArray();
                        if (ResolveMode(before) != ResolveMode(after) || changes.Length != 0)
                            throw new InvalidOperationException("风扇操作期间模式或其他配置发生变化，请核对原厂状态。");
                        if (FanBoostEnabled == enabled)
                        {
                            if (first is null) { first = after; firstSequence = _statusSequence; }
                            else if (_statusSequence > firstSequence && _clock.GetElapsedTime(first.ReceivedTimestamp, after.ReceivedTimestamp) >= TimeSpan.FromMilliseconds(500))
                            {
                                _fanJournal = _fanJournal with { Confirmed = after };
                                FanRecord("Confirmed", enabled ? "已开启一键强冷，状态已两次回读确认。" : "已恢复原厂自动调速，状态已两次回读确认。");
                                return new(true, _fanJournal.Detail, after);
                            }
                        }
                        else { first = null; firstSequence = _statusSequence; }
                        sequence = _statusSequence;
                    }
                }
                if (_clock.GetElapsedTime(lastQuery) >= TimeSpan.FromSeconds(1)) { await Query(token); lastQuery = _clock.GetTimestamp(); }
                await Task.Delay(75, token);
            }
            throw new TimeoutException("风扇状态回读超时，请核对原厂当前状态。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            lock (_gate)
            {
                _fanUncertain |= sent;
                _fanJournal = _fanJournal with { Phase = _fanUncertain ? "Uncertain" : "Stopped", UpdatedAtUtc = _clock.GetUtcNow(),
                    Detail = _fanUncertain ? "风扇操作结果未确认，请核对原厂状态后重新连接；不会自动重发或恢复。" : ex.Message };
                try { _saveFan?.Invoke(_fanJournal); } catch (Exception save) when (save is IOException or UnauthorizedAccessException) { }
                return new(false, _fanJournal.Detail, null);
            }
        }
        finally { Interlocked.Exchange(ref _fanBusy, 0); Interlocked.Exchange(ref _busy, 0); }
    }
}
