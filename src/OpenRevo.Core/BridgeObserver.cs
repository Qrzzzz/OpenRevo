using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace OpenRevo.Core;

public sealed record RequestTrace(DateTimeOffset SentAtUtc, string Topic, string Action);
public enum MonitorPhase { Idle, Connecting, Authenticating, Subscribing, WaitingCapabilities, Monitoring, Stopped }
internal sealed record ObserverTiming(TimeSpan Pulse, TimeSpan Ping, TimeSpan Refresh, TimeSpan CapabilityLifetime)
{
    internal static ObserverTiming Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45));
}

/// <summary>
/// One finite session; audited queries and typed, separately gated mode commands.
/// Does not expose raw publish, remote hosts, driver access or automatic reconnect.
/// </summary>
public sealed class BridgeObserver
{
    private readonly ConcurrentQueue<RequestTrace> _trace = new();
    private int _refresh;
    private int _suspending;
    private CancellationTokenSource? _sessionStop;
    public SessionHealth Health { get; }
    private readonly TimeProvider _clock;
    private readonly ObserverTiming _timing;
    public MonitorPhase Phase { get; private set; }
    public MonitoringDiagnostic? LastDiagnostic { get; private set; }
    public ModePilot Pilot { get; }
    public BridgeObserver() : this(TimeProvider.System, ObserverTiming.Default) { }
    internal BridgeObserver(TimeProvider clock, ObserverTiming timing, bool auditModes = false)
    { _clock = clock; _timing = timing; Pilot = new ModePilot(clock, auditModes); Health = new SessionHealth(clock); }
    public IReadOnlyList<RequestTrace> Requests => _trace.ToArray();
    public void NotifyPowerSource(bool ac)
    {
        Health.RecordEvent("WindowsPowerSource", ac ? "Windows：交流电" : "Windows：电池");
        Pilot.UpdatePowerSource(ac);
    }
    public void NotifySuspend()
    {
        Interlocked.Exchange(ref _suspending, 1);
        Health.RecordEvent("Suspending", "系统即将休眠，结束当前连接；不恢复或重放模式命令");
        Pilot.End();
        try { Volatile.Read(ref _sessionStop)?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void NotifyResume() => Health.RecordEvent("Resumed", "系统已唤醒；需要时手动重新连接");
    public void RequestRefresh() => Interlocked.Exchange(ref _refresh, 1);

    public Task ObserveAsync(BridgeInstallation installation, Action<TopicSample> onMessage, Action<string> onState,
        CancellationToken cancellationToken) => ObserveAtAsync(installation.Password, new IPEndPoint(IPAddress.Loopback, ReadProtocol.Port),
            onMessage, onState, cancellationToken, useSlotLock: true);

    internal async Task ObserveAtAsync(string password, IPEndPoint endpoint, Action<TopicSample> onMessage, Action<string> onState,
        CancellationToken cancellationToken, bool useSlotLock)
    {
        if (!IPAddress.IsLoopback(endpoint.Address)) throw new InvalidOperationException("Only loopback is supported.");
        Interlocked.Exchange(ref _refresh, 0);
        LastDiagnostic = null;
        Health.Begin();
        Phase = MonitorPhase.Connecting;
        var lease = new TelemetryLease(_clock, _timing.CapabilityLifetime);
        try
        {
        using var slot = useSlotLock ? AcquireSlot() : null;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writeLock = new SemaphoreSlim(1, 1);
        var token = lifetime.Token;
        Volatile.Write(ref _sessionStop, lifetime);
        if (Volatile.Read(ref _suspending) != 0) lifetime.Cancel();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(endpoint, timeout.Token);
        }
        await using var stream = client.GetStream();
        async Task Send(byte[] bytes, CancellationToken ct, Action? beforeWrite = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await writeLock.WaitAsync(timeout.Token);
            try { beforeWrite?.Invoke(); await stream.WriteAsync(bytes, timeout.Token); }
            finally { writeLock.Release(); }
        }
        async Task Query(ReadRequest request, CancellationToken? caller = null)
        {
            if (request == ReadRequest.EnableTelemetry && !lease.Demand()) return;
            await Send(MqttWire.Query(request), caller ?? token);
            Health.Request(request);
            var (topic, action) = ReadProtocol.Describe(request);
            _trace.Enqueue(new(DateTimeOffset.UtcNow, topic, action));
            while (_trace.Count > 256) _trace.TryDequeue(out _);
        }
        Phase = MonitorPhase.Authenticating;
        await Send(MqttWire.Connect(password), token);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var connack = await MqttWire.ReadAsync(stream, timeout.Token);
            if (connack.Header != 0x20 || !connack.Body.AsSpan().SequenceEqual(new byte[] { 0, 0 }))
                throw new MonitoringException(DiagnosticCode.AuthenticationRejected);
        }
        Phase = MonitorPhase.Subscribing;
        await Send(MqttWire.Subscribe(), token);
        var subscribed = false;
        var connectedAt = _clock.GetTimestamp();
        long pingSentAt = connectedAt;
        var awaitingPong = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReadLoop()
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(subscribed ? TimeSpan.FromSeconds(35) : TimeSpan.FromSeconds(5));
                var packet = await MqttWire.ReadAsync(stream, timeout.Token);
                switch (packet.Header >> 4)
                {
                    case 9:
                        if (subscribed || packet.Header != 0x90 || packet.Body.Length != ReadProtocol.Topics.Count + 2
                            || packet.Body[0] != 0 || packet.Body[1] != 1 || packet.Body.Skip(2).Any(x => x != 0))
                            throw new MonitoringException(DiagnosticCode.SubscriptionRejected);
                        subscribed = true;
                        Phase = MonitorPhase.WaitingCapabilities;
                        Pilot.Bind(async (mode, ct) =>
                        {
                            await Send(MqttWire.Mode(mode), ct, () =>
                            {
                                if (!lease.Demand() || Phase != MonitorPhase.Monitoring) throw new InvalidOperationException("监控授权已失效。");
                                Pilot.CheckSending();
                            });
                            var action = ModePilotPolicy.Action(mode);
                            _trace.Enqueue(new(DateTimeOffset.UtcNow, "Fan/Control", action));
                            while (_trace.Count > 256) _trace.TryDequeue(out _);
                        }, async ct => { await Query(ReadRequest.Capabilities, ct); await Query(ReadRequest.FanStatus, ct); },
                            () => Phase == MonitorPhase.Monitoring && lease.Demand(), token);
                        Pilot.BindFan(async (enabled, ct) =>
                        {
                            await Send(MqttWire.FanBoost(enabled), ct, () =>
                            {
                                if (!lease.Demand() || Phase != MonitorPhase.Monitoring) throw new InvalidOperationException("监控授权已失效。");
                                Pilot.CheckFanSending();
                            });
                            _trace.Enqueue(new(DateTimeOffset.UtcNow, "Fan/Control", FanBoostPolicy.Action(enabled)));
                            while (_trace.Count > 256) _trace.TryDequeue(out _);
                        });
                        onState("已连接 · 等待实时能力确认");
                        await Query(ReadRequest.Capabilities);
                        await Query(ReadRequest.FanStatus);
                        ready.TrySetResult();
                        break;
                    case 3:
                        var sample = MqttWire.ParsePublish(packet, _clock.GetUtcNow()) with { ReceivedTimestamp = _clock.GetTimestamp() };
                        Health.Observe(sample);
                        onMessage(sample);
                        var firstCapability = subscribed && lease.Observe(sample);
                        Pilot.Observe(sample);
                        if (firstCapability)
                        {
                            await Query(ReadRequest.EnableTelemetry);
                            Phase = MonitorPhase.Monitoring;
                            onState("已连接 · 监测中");
                        }
                        break;
                    case 13:
                        if (packet.Header != 0xd0 || packet.Body.Length != 0) throw new InvalidDataException("Malformed PINGRESP.");
                        Interlocked.Exchange(ref awaitingPong, 0);
                        break;
                    default:
                        throw new InvalidDataException("Unexpected packet from bridge.");
                }
            }
        }
        async Task PulseLoop()
        {
            await ready.Task.WaitAsync(token);
            var lastPing = connectedAt;
            var lastRefresh = _clock.GetTimestamp();
            while (true)
            {
                await Task.Delay(_timing.Pulse, token);
                var now = _clock.GetTimestamp();
                var authorized = lease.Demand();
                if (!authorized && _clock.GetElapsedTime(connectedAt, now) > _timing.CapabilityLifetime)
                    throw new MonitoringException(DiagnosticCode.CapabilityExpired);
                if (Volatile.Read(ref awaitingPong) != 0 && _clock.GetElapsedTime(Interlocked.Read(ref pingSentAt), now) > _timing.Ping * 2)
                    throw new MonitoringException(DiagnosticCode.ResponseTimeout);
                if (Volatile.Read(ref awaitingPong) == 0 && _clock.GetElapsedTime(lastPing, now) >= _timing.Ping)
                {
                    Interlocked.Exchange(ref pingSentAt, now); Interlocked.Exchange(ref awaitingPong, 1);
                    await Send([0xc0, 0], token); lastPing = now;
                }
                var manual = Interlocked.Exchange(ref _refresh, 0) != 0;
                if (manual || _clock.GetElapsedTime(lastRefresh, now) >= _timing.Refresh)
                {
                    await Query(ReadRequest.Capabilities);
                    await Query(ReadRequest.FanStatus);
                    // A manual refresh can restart shared telemetry if the vendor UI stopped it.
                    // Never send System_OFF: the timer has no per-client ownership contract.
                    if (manual && authorized) await Query(ReadRequest.EnableTelemetry);
                    lastRefresh = now;
                }
            }
        }
        var reader = ReadLoop();
        var pulse = PulseLoop();
        try { await await Task.WhenAny(reader, pulse); }
        finally
        {
            lifetime.Cancel();
            Pilot.End();
            // The session is clean and has no will or retained outgoing messages.
            try
            {
                using var end = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await Send([0xe0, 0], end.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            client.Close();
            try { await Task.WhenAll(reader, pulse); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException or MonitoringException) { }
        }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _suspending) != 0)
        { LastDiagnostic = MonitoringDiagnostic.Create(DiagnosticCode.SystemSuspended); throw new MonitoringException(DiagnosticCode.SystemSuspended); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { LastDiagnostic = MonitoringDiagnostic.Create(DiagnosticCode.Cancelled); throw; }
        catch (Exception exception) { LastDiagnostic = MonitoringDiagnostic.From(exception); throw; }
        finally { Volatile.Write(ref _sessionStop, null); Phase = MonitorPhase.Stopped; Pilot.End(); Health.Stop(LastDiagnostic?.Code); }
    }

    private static FileStream AcquireSlot()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "bridge-slot.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new MonitoringException(DiagnosticCode.SlotOccupied); }
    }
}
