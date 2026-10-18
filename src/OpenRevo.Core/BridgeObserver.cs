using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace OpenRevo.Core;

public sealed record RequestTrace(DateTimeOffset SentAtUtc, string Topic, string Action);

/// <summary>
/// One finite session; exact subscriptions and three audited read requests only.
/// Does not expose raw publish, remote hosts, driver access or automatic reconnect.
/// </summary>
public sealed class BridgeObserver
{
    private readonly ConcurrentQueue<RequestTrace> _trace = new();
    private int _refresh;
    private int _telemetryAllowed;
    public IReadOnlyList<RequestTrace> Requests => _trace.ToArray();
    public void RequestRefresh() => Interlocked.Exchange(ref _refresh, 1);

    public Task ObserveAsync(BridgeInstallation installation, Action<TopicSample> onMessage, Action<string> onState,
        CancellationToken cancellationToken) => ObserveAtAsync(installation.Password, new IPEndPoint(IPAddress.Loopback, ReadProtocol.Port),
            onMessage, onState, cancellationToken, useSlotLock: true);

    internal async Task ObserveAtAsync(string password, IPEndPoint endpoint, Action<TopicSample> onMessage, Action<string> onState,
        CancellationToken cancellationToken, bool useSlotLock)
    {
        if (!IPAddress.IsLoopback(endpoint.Address)) throw new InvalidOperationException("Only loopback is supported.");
        Interlocked.Exchange(ref _telemetryAllowed, 0);
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
            await Send(MqttWire.Query(request), token);
            var (topic, action) = ReadProtocol.Describe(request);
            _trace.Enqueue(new(DateTimeOffset.UtcNow, topic, action));
            while (_trace.Count > 256) _trace.TryDequeue(out _);
        }
        await Send(MqttWire.Connect(password), token);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var connack = await MqttWire.ReadAsync(stream, timeout.Token);
            if (connack.Header != 0x20 || !connack.Body.AsSpan().SequenceEqual(new byte[] { 0, 0 }))
                throw new InvalidDataException("桥接服务未接受连接，或会话不符合 clean-session 要求。" );
        }
        await Send(MqttWire.Subscribe(), token);
        var subscribed = false;
        var connectedAt = DateTimeOffset.UtcNow;
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
                            throw new InvalidDataException("订阅未完整接受，已停止读取。" );
                        subscribed = true;
                        onState("已连接 · 只读监测");
                        await Query(ReadRequest.Capabilities);
                        await Query(ReadRequest.FanStatus);
                        ready.TrySetResult();
                        break;
                    case 3:
                        var sample = MqttWire.ParsePublish(packet, DateTimeOffset.UtcNow);
                        onMessage(sample);
                        if (subscribed && DeviceAudit.MatchesAuditedTelemetry(sample) && Interlocked.CompareExchange(ref _telemetryAllowed, 1, 0) == 0)
                            await Query(ReadRequest.EnableTelemetry);
                        break;
                    case 13:
                        if (packet.Header != 0xd0 || packet.Body.Length != 0) throw new InvalidDataException("Malformed PINGRESP.");
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
            var lastRefresh = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(1000, token);
                var now = DateTimeOffset.UtcNow;
                if (now - lastPing >= TimeSpan.FromSeconds(10)) { await Send([0xc0, 0], token); lastPing = now; }
                var manual = Interlocked.Exchange(ref _refresh, 0) != 0;
                if (manual || now - lastRefresh >= TimeSpan.FromSeconds(15))
                {
                    await Query(ReadRequest.Capabilities);
                    await Query(ReadRequest.FanStatus);
                    // A manual refresh can restart shared telemetry if the vendor UI stopped it.
                    // Never send System_OFF: the timer has no per-client ownership contract.
                    if (manual && Volatile.Read(ref _telemetryAllowed) == 1) await Query(ReadRequest.EnableTelemetry);
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
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
        }
    }

    private static FileStream AcquireSlot()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevo");
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "bridge-slot.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("已有 OpenRevo 观察会话占用本机连接，请先断开另一个窗口。", ex); }
    }
}
