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
/// One finite session; exact subscriptions and three audited read requests only.
/// Does not expose raw publish, remote hosts, driver access or automatic reconnect.
/// </summary>
public sealed class BridgeObserver
{
    private readonly ConcurrentQueue<RequestTrace> _trace = new();
    private int _refresh;
    private readonly TimeProvider _clock;
    private readonly ObserverTiming _timing;
    public MonitorPhase Phase { get; private set; }
    public MonitoringDiagnostic? LastDiagnostic { get; private set; }
    public BridgeObserver() : this(TimeProvider.System, ObserverTiming.Default) { }
    internal BridgeObserver(TimeProvider clock, ObserverTiming timing) { _clock = clock; _timing = timing; }
    public IReadOnlyList<RequestTrace> Requests => _trace.ToArray();
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
        Phase = MonitorPhase.Connecting;
        var lease = new TelemetryLease(_clock, _timing.CapabilityLifetime);
        try
        {
        using var slot = useSlotLock ? AcquireSlot() : null;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writeLock = new SemaphoreSlim(1, 1);
        var token = lifetime.Token;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(endpoint, timeout.Token);
        }
        await using var stream = client.GetStream();
        async Task Send(byte[] bytes, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await writeLock.WaitAsync(timeout.Token);
            try { await stream.WriteAsync(bytes, timeout.Token); }
            finally { writeLock.Release(); }
        }
        async Task Query(ReadRequest request)
        {
            if (request == ReadRequest.EnableTelemetry && !lease.Demand()) return;
            await Send(MqttWire.Query(request), token);
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
                        onState("已连接 · 等待实时能力确认");
                        await Query(ReadRequest.Capabilities);
                        await Query(ReadRequest.FanStatus);
                        ready.TrySetResult();
                        break;
                    case 3:
                        var sample = MqttWire.ParsePublish(packet, _clock.GetUtcNow()) with { ReceivedTimestamp = _clock.GetTimestamp() };
                        onMessage(sample);
                        if (subscribed && lease.Observe(sample))
                        {
                            await Query(ReadRequest.EnableTelemetry);
                            Phase = MonitorPhase.Monitoring;
                            onState("已连接 · 只读监测");
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { LastDiagnostic = MonitoringDiagnostic.Create(DiagnosticCode.Cancelled); throw; }
        catch (Exception exception) { LastDiagnostic = MonitoringDiagnostic.From(exception); throw; }
        finally { Phase = MonitorPhase.Stopped; }
    }

    private static FileStream AcquireSlot()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "bridge-slot.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new MonitoringException(DiagnosticCode.SlotOccupied); }
    }
}
